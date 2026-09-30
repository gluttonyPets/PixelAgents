using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;
using Server.Services.Ai;
using Server.Services.Ai.Handlers;
using Server.Services.GoogleDrive;

namespace Server.Services.Triggers;

/// <summary>
/// Vigila los pipelines de tipo Trigger y los ejecuta cuando ocurre su evento. Hoy
/// solo existe "Nuevo archivo en Google Drive": cada pipeline consulta su carpeta
/// cada <see cref="DriveTriggerConfig.PollMinutes"/> minutos y lanza una ejecucion
/// por cada archivo que no habia visto antes.
/// </summary>
public class TriggerBackgroundService : BackgroundService
{
    public const string DriveApiKeyProvider = "GoogleDrive";

    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _sp;
    private readonly ILogger<TriggerBackgroundService> _log;
    private readonly ExecutionCancellationService _cancellation;

    // Un pipeline se procesa de uno en uno: mientras sus ejecuciones siguen en marcha
    // no se vuelve a consultar su carpeta (y no se pisan entre ellas).
    private readonly ConcurrentDictionary<(string Db, Guid ProjectId), byte> _running = new();

    public TriggerBackgroundService(IServiceProvider sp, ILogger<TriggerBackgroundService> log,
        ExecutionCancellationService cancellation)
    {
        _sp = sp;
        _log = log;
        _cancellation = cancellation;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("TriggerBackgroundService started");
        while (!stoppingToken.IsCancellationRequested)
        {
            // Nada de lo que pase aqui puede escapar: una excepcion no capturada en un
            // BackgroundService para TODA la aplicacion (StopHost). Solo se sale al apagar.
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _log.LogError(ex, "Trigger tick failed"); }
            catch (OperationCanceledException) { break; }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var coreDb = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
        var tenants = await coreDb.Accounts.Select(a => a.DbName).ToListAsync(ct);
        var now = DateTime.UtcNow;

        foreach (var dbName in tenants)
        {
            try
            {
                await using var db = factory.Create(dbName);
                var projects = await db.Projects
                    .Where(p => p.DeletedAt == null
                        && p.ProjectType == ProjectTypes.Trigger
                        && p.TriggerType == TriggerTypes.DriveNewFile)
                    .Select(p => new { p.Id, p.TriggerConfig })
                    .ToListAsync(ct);
                if (projects.Count == 0) continue;

                var ids = projects.Select(p => p.Id).ToList();
                var lastChecks = await db.ProjectTriggerStates
                    .Where(s => ids.Contains(s.ProjectId))
                    .ToDictionaryAsync(s => s.ProjectId, s => s.LastCheckedAt, ct);

                foreach (var p in projects)
                {
                    var interval = DriveTriggerConfig.Parse(p.TriggerConfig).PollInterval;
                    if (lastChecks.TryGetValue(p.Id, out var last) && last is { } l && now - l < interval)
                        continue;

                    var key = (dbName, p.Id);
                    if (!_running.TryAdd(key, 0)) continue;
                    _ = Task.Run(async () =>
                    {
                        try { await ProcessDriveProjectAsync(dbName, p.Id, ct); }
                        finally { _running.TryRemove(key, out _); }
                    });
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogError(ex, "Trigger tick failed for tenant {Db}", dbName);
            }
        }
    }

    private async Task ProcessDriveProjectAsync(string dbName, Guid projectId, CancellationToken ct)
    {
        try
        {
            using var scope = _sp.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
            var drive = scope.ServiceProvider.GetRequiredService<GoogleDriveService>();
            await using var db = factory.Create(dbName);

            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId && p.DeletedAt == null, ct);
            if (project is null || project.ProjectType != ProjectTypes.Trigger || project.TriggerType != TriggerTypes.DriveNewFile)
                return;

            var state = await db.ProjectTriggerStates.FirstOrDefaultAsync(s => s.ProjectId == projectId, ct);
            if (state is null)
            {
                state = new ProjectTriggerState { ProjectId = projectId, ConfigKey = "", UpdatedAt = DateTime.UtcNow };
                db.ProjectTriggerStates.Add(state);
            }
            var now = DateTime.UtcNow;
            state.LastCheckedAt = now;
            state.UpdatedAt = now;

            var config = DriveTriggerConfig.Parse(project.TriggerConfig);
            var configError = config.Validate();
            ApiKey? apiKey = null;
            if (configError is null)
            {
                apiKey = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == config.ApiKeyId, ct);
                if (apiKey is null || apiKey.ProviderType != DriveApiKeyProvider)
                    configError = "La API key de Google Drive elegida ya no existe.";
            }
            if (configError is not null)
            {
                state.LastError = configError;
                await db.SaveChangesAsync(ct);
                return;
            }

            var folderId = config.FolderId!;
            DriveFolderInfo folder;
            List<DriveFile> listed;
            try
            {
                folder = await drive.GetFolderAsync(apiKey!.EncryptedKey, folderId, ct);
                listed = await drive.ListFilesAsync(apiKey.EncryptedKey, folderId, ct);
            }
            catch (Exception ex) when (ex is GoogleDriveException or HttpRequestException)
            {
                state.LastError = ex is GoogleDriveException
                    ? ex.Message
                    : $"No se pudo conectar con Google Drive: {ex.Message}";
                await db.SaveChangesAsync(ct);
                return;
            }

            var configKey = DriveTriggerPlanner.ConfigKey(folderId);
            var seen = (await db.TriggerSeenItems
                    .Where(i => i.ProjectId == projectId)
                    .Select(i => i.ItemKey)
                    .ToListAsync(ct))
                .ToHashSet();
            var plan = DriveTriggerPlanner.Plan(state.ConfigKey, state.BaselineAt is not null, configKey, listed, seen);
            state.LastError = null;

            if (plan.TakeBaseline)
            {
                // Lo que ya estaba en la carpeta no dispara: solo se toma nota.
                await db.TriggerSeenItems.Where(i => i.ProjectId == projectId).ExecuteDeleteAsync(ct);
                db.TriggerSeenItems.AddRange(listed
                    .Select(f => DriveTriggerPlanner.ItemKey(f.Id))
                    .Distinct()
                    .Select(k => new TriggerSeenItem { ProjectId = projectId, ItemKey = k, SeenAt = now }));
                state.ConfigKey = configKey;
                state.BaselineAt = now;
                await db.SaveChangesAsync(ct);
                _log.LogInformation("Drive trigger {ProjectId}: linea base con {Count} archivo(s) en '{Folder}'",
                    projectId, listed.Count, folder.Name);
                return;
            }

            await db.SaveChangesAsync(ct);

            foreach (var file in plan.NewFiles)
            {
                ct.ThrowIfCancellationRequested();
                // El archivo se marca como visto ANTES de ejecutar: si otra instancia ya
                // lo reclamo, no se repite; si la ejecucion falla, no se reintenta en bucle.
                if (!await ClaimAsync(db, projectId, DriveTriggerPlanner.ItemKey(file.Id), ct))
                    continue;

                await FireAsync(dbName, projectId, drive, apiKey!.EncryptedKey, config, folder, file, ct);

                await db.ProjectTriggerStates
                    .Where(s => s.ProjectId == projectId)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(s => s.LastFiredAt, DateTime.UtcNow)
                        .SetProperty(s => s.UpdatedAt, DateTime.UtcNow), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Drive trigger failed for project {ProjectId} ({Db})", projectId, dbName);
        }
    }

    /// <summary>Registra el elemento como visto; false si ya lo estaba.</summary>
    private static async Task<bool> ClaimAsync(UserDbContext db, Guid projectId, string itemKey, CancellationToken ct)
    {
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""TriggerSeenItems"" (""ProjectId"", ""ItemKey"", ""SeenAt"")
            VALUES ({projectId}, {itemKey}, {DateTime.UtcNow})
            ON CONFLICT DO NOTHING", ct);
        return inserted > 0;
    }

    private async Task FireAsync(string dbName, Guid projectId, GoogleDriveService drive, string credentialsJson,
        DriveTriggerConfig config, DriveFolderInfo folder, DriveFile file, CancellationToken ct)
    {
        var files = new List<ProducedFile>();
        string? note = null;
        if (config.DownloadFile)
        {
            try
            {
                var download = await drive.DownloadAsync(credentialsJson, file, ct);
                if (download is null)
                    note = "(El archivo no se adjunta: es demasiado grande o de un tipo que no se puede descargar.)";
                else
                    files.Add(new ProducedFile { Data = download.Data, FileName = download.FileName, ContentType = download.ContentType });
            }
            catch (GoogleDriveException ex)
            {
                note = $"(El archivo no se pudo adjuntar: {ex.Message})";
                _log.LogWarning("Drive trigger {ProjectId}: {Error}", projectId, ex.Message);
            }
        }

        var userInput = DriveTriggerPlanner.BuildUserInput(file, folder.Name, note);
        _log.LogInformation("Drive trigger {ProjectId}: archivo nuevo '{File}' ({FileId}), lanzando pipeline",
            projectId, file.Name, file.Id);

        using var scope = _sp.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IPipelineExecutor>();
        var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
        await using var execDb = factory.Create(dbName);

        // Igual que las programadas: el boton "Cancelar" del pipeline puede pararla.
        var userToken = _cancellation.Register(projectId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(userToken, ct);
        try
        {
            await executor.ExecuteAsync(projectId, userInput, execDb, dbName, linked.Token,
                inputFiles: files);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogError(ex, "Drive trigger {ProjectId}: la ejecucion por '{File}' fallo", projectId, file.Name);
        }
        finally
        {
            _cancellation.Remove(projectId);
        }
    }
}
