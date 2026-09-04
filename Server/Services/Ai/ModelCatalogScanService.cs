using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;

namespace Server.Services.Ai;

public interface IModelCatalogScanService
{
    /// <summary>
    /// Lanza una pasada de deteccion: compara el catalogo y las tarifas de hoy contra
    /// la ultima foto guardada, pregunta a los proveedores que modelos tienen, apunta
    /// las diferencias en el historico y deja la foto actualizada.
    /// </summary>
    Task<ModelScanResultResponse> RunScanAsync(
        UserDbContext db, string trigger = "manual", CancellationToken ct = default);

    /// <summary>Ultimas ejecuciones del servicio y ultimos cambios que ha detectado.</summary>
    Task<ModelScanHistoryResponse> GetHistoryAsync(
        UserDbContext db, int runLimit = 20, int changeLimit = 200, CancellationToken ct = default);
}

/// <summary>
/// Detecta y registra cambios en el catalogo de modelos.
///
/// Responde a dos preguntas distintas con dos fuentes distintas:
///
/// 1. <b>¿Hay modelos nuevos?</b> Se le pregunta a la API del proveedor
///    (<see cref="ModelAvailabilityService"/>). Todo id que el proveedor lista y el
///    catalogo del repo no conoce sale como <c>provider_new_model</c>: es la señal de
///    que hay que dar de alta el modelo en <see cref="ModelCatalog"/>, con su tarifa.
///
/// 2. <b>¿Han cambiado los precios?</b> Aqui no hay API que preguntar —ningun proveedor
///    publica sus tarifas— asi que se compara la tabla del repo contra la foto que dejo
///    el escaneo anterior. Cuando alguien revisa <see cref="PricingCatalog"/> y despliega,
///    la siguiente pasada detecta exactamente que modelo cambio, de cuanto a cuanto y
///    que dia; sin esta foto esa informacion solo estaria en el historial de git.
///
/// En ambos casos "actualizar" significa dejar la foto al dia (<see cref="ModelCatalogSnapshot"/>),
/// no reescribir la tabla de tarifas: el precio con el que se factura sigue saliendo del
/// codigo revisado a mano, que es la unica fuente en la que se puede confiar para cobrar.
/// </summary>
public class ModelCatalogScanService : IModelCatalogScanService
{
    /// <summary>
    /// Tope de modelos desconocidos que se apuntan por pasada. OpenAI lista mas de
    /// cien ids (snapshots viejos, moderacion, modelos que la app no usa) y volcarlos
    /// todos convertiria el historico en ruido. Lo que no cabe se cuenta en la nota.
    /// </summary>
    private const int MaxProviderFindingsPerScan = 40;

    private readonly IModelAvailabilityService _availability;
    private readonly ILogger<ModelCatalogScanService>? _log;

    public ModelCatalogScanService(
        IModelAvailabilityService availability,
        ILogger<ModelCatalogScanService>? log = null)
    {
        _availability = availability;
        _log = log;
    }

    public async Task<ModelScanResultResponse> RunScanAsync(
        UserDbContext db, string trigger = "manual", CancellationToken ct = default)
    {
        var run = new ModelScanRun
        {
            Id = Guid.NewGuid(),
            StartedAt = DateTime.UtcNow,
            Trigger = string.IsNullOrWhiteSpace(trigger) ? "manual" : trigger,
        };

        var changes = new List<ModelCatalogChange>();

        try
        {
            var snapshots = await db.ModelCatalogSnapshots.ToListAsync(ct);
            var byId = snapshots.ToDictionary(s => s.ModelId, StringComparer.OrdinalIgnoreCase);

            // Sin foto previa no hay nada contra lo que comparar: la primera pasada
            // solo fotografia. Si generase historico, el dia que se estrena la pantalla
            // apareceria el catalogo entero como "modelo nuevo" y el historico nacería
            // inservible.
            run.IsBaseline = snapshots.Count == 0;

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var upstream = await QueryProvidersAsync(db, ct);
            run.ProvidersQueried = upstream.Count > 0
                ? string.Join(", ", upstream.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                : null;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ── 1. Catalogo del repo: altas, cambios de tarifa y de ciclo de vida ──
            foreach (var model in ModelCatalog.AllModels)
            {
                ct.ThrowIfCancellationRequested();
                seen.Add(model.Id);

                var rates = ModelCatalogService.ResolveRates(model);
                var status = ModelLifecycle.Resolve(model.Id, today).Status
                    .ToString().ToLowerInvariant();
                var available = ResolveUpstream(upstream, model.Provider, model.Id);

                if (!byId.TryGetValue(model.Id, out var snap))
                {
                    db.ModelCatalogSnapshots.Add(
                        NewSnapshot(model, rates, status, available, "catalog"));

                    if (!run.IsBaseline)
                    {
                        changes.Add(Change(run, model.Id, model.Provider, "new_model", null, null,
                            Describe(rates),
                            $"{model.DisplayName} entra en el catalogo."));
                        run.NewModels++;
                    }
                    continue;
                }

                // Un modelo que el proveedor listaba y ya no lista, o al reves. Solo se
                // apunta la transicion: repetirlo en cada pasada no aporta nada nuevo.
                if (!run.IsBaseline && available is not null && snap.AvailableUpstream != available)
                {
                    changes.Add(Change(run, model.Id, model.Provider, "availability_change",
                        "AvailableUpstream",
                        snap.AvailableUpstream?.ToString().ToLowerInvariant(),
                        available.Value.ToString().ToLowerInvariant(),
                        available.Value
                            ? $"{model.Provider} vuelve a listar {model.Id}."
                            : $"{model.Provider} ya no lista {model.Id} con la key configurada."));
                }

                if (!run.IsBaseline && !string.Equals(snap.LifecycleStatus, status, StringComparison.OrdinalIgnoreCase))
                {
                    changes.Add(Change(run, model.Id, model.Provider, "lifecycle_change",
                        "LifecycleStatus", snap.LifecycleStatus, status,
                        $"{model.DisplayName} pasa de {snap.LifecycleStatus} a {status}."));
                }

                if (!run.IsBaseline)
                {
                    foreach (var diff in PriceDiffs(snap, rates))
                    {
                        changes.Add(Change(run, model.Id, model.Provider, "price_change",
                            diff.Field, Money(diff.Old), Money(diff.New),
                            $"{model.DisplayName}: {diff.Label} pasa de {Money(diff.Old)} a " +
                            $"{Money(diff.New)}{PercentSuffix(diff.Old, diff.New)}."));
                        run.PriceChanges++;
                    }
                }

                Apply(snap, model, rates, status, available);
            }

            // ── 2. Bajas: lo que estaba en la foto y ya no esta en el catalogo ──
            foreach (var snap in snapshots.Where(s =>
                         s.Source == "catalog" && !seen.Contains(s.ModelId)))
            {
                if (!run.IsBaseline)
                {
                    changes.Add(Change(run, snap.ModelId, snap.Provider, "removed_model",
                        null, Describe(snap), null,
                        $"{snap.DisplayName} ya no esta en el catalogo."));
                }

                db.ModelCatalogSnapshots.Remove(snap);
            }

            // ── 3. Modelos que el proveedor lista y el catalogo no conoce ──
            run.NewModels += RegisterProviderFindings(db, run, changes, upstream, byId);

            run.ModelsScanned = ModelCatalog.AllModels.Length;
            run.ChangesDetected = changes.Count;
            run.FinishedAt = DateTime.UtcNow;

            db.ModelScanRuns.Add(run);
            db.ModelCatalogChanges.AddRange(changes);
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Un fallo tambien es historico: si el escaneo lleva tres semanas cayendo,
            // el usuario tiene que poder verlo en la misma lista, no en los logs.
            _log?.LogWarning(ex, "Fallo el escaneo del catalogo de modelos");

            run.Status = "error";
            run.Error = ex.Message;
            run.FinishedAt = DateTime.UtcNow;

            db.ChangeTracker.Clear();
            db.ModelScanRuns.Add(run);
            await db.SaveChangesAsync(ct);

            return new ModelScanResultResponse(ToDto(run), []);
        }

        return new ModelScanResultResponse(ToDto(run), changes.Select(ToDto).ToList());
    }

    public async Task<ModelScanHistoryResponse> GetHistoryAsync(
        UserDbContext db, int runLimit = 20, int changeLimit = 200, CancellationToken ct = default)
    {
        var runs = await db.ModelScanRuns
            .OrderByDescending(r => r.StartedAt)
            .Take(Math.Clamp(runLimit, 1, 200))
            .ToListAsync(ct);

        var changes = await db.ModelCatalogChanges
            .OrderByDescending(c => c.DetectedAt)
            .Take(Math.Clamp(changeLimit, 1, 1000))
            .ToListAsync(ct);

        return new ModelScanHistoryResponse(
            runs.Count > 0 ? ToDto(runs[0]) : null,
            runs.Select(ToDto).ToList(),
            changes.Select(ToDto).ToList());
    }

    // ────────────────────────────── deteccion ──────────────────────────────

    /// <summary>
    /// Ids que cada proveedor lista para las keys del tenant. Solo aparecen los
    /// proveedores a los que se les pudo preguntar de verdad: un proveedor ausente
    /// significa "no lo se", y sin esa distincion el escaneo daria por retirado todo
    /// el catalogo de un proveedor cada vez que su API tuviese un mal dia.
    /// </summary>
    private async Task<Dictionary<string, IReadOnlySet<string>>> QueryProvidersAsync(
        UserDbContext db, CancellationToken ct)
    {
        var keys = await db.ApiKeys
            .Where(k => k.EncryptedKey != null && k.EncryptedKey != "")
            .Select(k => new { k.ProviderType, k.EncryptedKey })
            .ToListAsync(ct);

        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in keys.GroupBy(k => k.ProviderType, StringComparer.OrdinalIgnoreCase))
        {
            var ids = await _availability.GetAvailableModelIdsAsync(
                group.Key, group.First().EncryptedKey, ct);

            if (ids is not null) result[group.Key] = ids;
        }

        return result;
    }

    /// <summary>
    /// Apunta los ids que el proveedor lista y el catalogo no tiene. Devuelve cuantos
    /// se han registrado como novedad.
    /// </summary>
    private static int RegisterProviderFindings(
        UserDbContext db,
        ModelScanRun run,
        List<ModelCatalogChange> changes,
        Dictionary<string, IReadOnlySet<string>> upstream,
        Dictionary<string, ModelCatalogSnapshot> byId)
    {
        var known = ModelCatalog.AllModels
            .Select(m => m.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var registered = 0;
        var skipped = 0;

        // Dos proveedores pueden listar el mismo id (los agregadores reexponen modelos
        // de terceros). El indice de ModelId es unico, asi que apuntarlo dos veces en la
        // misma pasada reventaria el guardado entero.
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (provider, ids) in upstream)
        {
            var candidates = ids
                .Where(id => !known.Contains(id))
                .Where(id => !added.Contains(id))
                // "gpt-5.6-sol-2026-03-11" es el mismo modelo que ya esta en el
                // catalogo, no uno nuevo: anunciarlo cada vez que OpenAI publica un
                // snapshot con fecha llenaria el historico de falsos positivos.
                .Where(id => !known.Contains(ModelLifecycle.StripSnapshotSuffix(id)))
                // Ya se anuncio en una pasada anterior: sigue sin estar en el catalogo,
                // pero no es una novedad de hoy.
                .Where(id => !byId.ContainsKey(id))
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var id in candidates)
            {
                if (registered >= MaxProviderFindingsPerScan)
                {
                    skipped += candidates.Count - candidates.IndexOf(id);
                    break;
                }

                db.ModelCatalogSnapshots.Add(new ModelCatalogSnapshot
                {
                    Id = Guid.NewGuid(),
                    ModelId = id,
                    Provider = provider,
                    DisplayName = id,
                    Kind = "other",
                    Source = "provider",
                    AvailableUpstream = true,
                    FirstSeenAt = run.StartedAt,
                    LastSeenAt = run.StartedAt,
                });

                added.Add(id);
                registered++;

                if (run.IsBaseline) continue;

                changes.Add(Change(run, id, provider, "provider_new_model", null, null, id,
                    $"{provider} lista {id} y el catalogo no lo tiene. " +
                    "Para poder usarlo hay que darlo de alta en ModelCatalog.cs, " +
                    "Modules.razor y PricingCatalog.cs con su tarifa."));
            }
        }

        if (skipped > 0 && !run.IsBaseline)
        {
            changes.Add(Change(run, "-", "-", "provider_new_model", null, null, null,
                $"Hay {skipped} ids mas sin catalogar; se apuntaran en la siguiente pasada."));
        }

        return run.IsBaseline ? 0 : registered;
    }

    /// <summary>
    /// Tarifas que han cambiado entre la foto y el estado actual. Una fila por campo:
    /// que suba la entrada y baje la salida son dos hechos distintos.
    /// </summary>
    private static IEnumerable<(string Field, string Label, decimal? Old, decimal? New)> PriceDiffs(
        ModelCatalogSnapshot snap, ModelCatalogService.ModelRates rates)
    {
        (string Field, string Label, decimal? Old, decimal? New)[] candidates =
        [
            ("InputPerMTok",  "la entrada por 1M de tokens", snap.InputPerMTok,  rates.InputPerMTok),
            ("OutputPerMTok", "la salida por 1M de tokens",  snap.OutputPerMTok, rates.OutputPerMTok),
            ("ImageLow",      "la imagen en calidad baja",   snap.ImageLow,      rates.ImageLow),
            ("ImageMedium",   "la imagen en calidad media",  snap.ImageMedium,   rates.ImageMedium),
            ("ImageHigh",     "la imagen en calidad alta",   snap.ImageHigh,     rates.ImageHigh),
            ("AuxAmount",     $"el precio por {snap.AuxUnit ?? rates.AuxUnit ?? "uso"}",
                                                             snap.AuxAmount,     rates.AuxAmount),
        ];

        return candidates.Where(c => c.Old != c.New);
    }

    /// <summary>
    /// Si el proveedor lista el modelo. null cuando no se le ha podido preguntar, que
    /// es distinto de que haya dicho que no lo tiene.
    /// </summary>
    private static bool? ResolveUpstream(
        Dictionary<string, IReadOnlySet<string>> upstream, string provider, string modelId) =>
        upstream.TryGetValue(provider, out var ids) ? ids.Contains(modelId) : null;

    // ────────────────────────────── mapeo ──────────────────────────────

    private static ModelCatalogSnapshot NewSnapshot(
        ModelCatalog.CatalogModel model,
        ModelCatalogService.ModelRates rates,
        string status,
        bool? available,
        string source)
    {
        var snap = new ModelCatalogSnapshot
        {
            Id = Guid.NewGuid(),
            ModelId = model.Id,
            Source = source,
            FirstSeenAt = DateTime.UtcNow,
        };

        Apply(snap, model, rates, status, available);
        return snap;
    }

    private static void Apply(
        ModelCatalogSnapshot snap,
        ModelCatalog.CatalogModel model,
        ModelCatalogService.ModelRates rates,
        string status,
        bool? available)
    {
        snap.Provider = model.Provider;
        snap.DisplayName = model.DisplayName;
        snap.Kind = rates.Kind;
        snap.InputPerMTok = rates.InputPerMTok;
        snap.OutputPerMTok = rates.OutputPerMTok;
        snap.ImageLow = rates.ImageLow;
        snap.ImageMedium = rates.ImageMedium;
        snap.ImageHigh = rates.ImageHigh;
        snap.AuxAmount = rates.AuxAmount;
        snap.AuxUnit = rates.AuxUnit;
        snap.LifecycleStatus = status;
        snap.LastSeenAt = DateTime.UtcNow;

        // Una consulta fallida no borra lo que ya se sabia: null es "hoy no he podido
        // preguntar", y pisar el ultimo dato conocido con eso perderia la transicion.
        if (available is not null) snap.AvailableUpstream = available;
    }

    private static ModelCatalogChange Change(
        ModelScanRun run, string modelId, string provider, string type,
        string? field, string? oldValue, string? newValue, string? note) =>
        new()
        {
            Id = Guid.NewGuid(),
            ScanId = run.Id,
            ModelId = Truncate(modelId, 200),
            Provider = Truncate(provider, 100),
            ChangeType = type,
            Field = field,
            OldValue = Truncate(oldValue, 200),
            NewValue = Truncate(newValue, 200),
            Note = note,
            DetectedAt = run.StartedAt,
        };

    /// <summary>Resumen de tarifa en una linea, para las altas y las bajas.</summary>
    private static string Describe(ModelCatalogService.ModelRates r) => r.Kind switch
    {
        "text"  => $"{Money(r.InputPerMTok)} / {Money(r.OutputPerMTok)} por 1M tokens",
        "image" => $"{Money(r.ImageMedium)} por imagen",
        _       => r.AuxAmount is null ? "sin coste por uso" : $"{Money(r.AuxAmount)} por {r.AuxUnit}",
    };

    private static string Describe(ModelCatalogSnapshot s) => s.Kind switch
    {
        "text"  => $"{Money(s.InputPerMTok)} / {Money(s.OutputPerMTok)} por 1M tokens",
        "image" => $"{Money(s.ImageMedium)} por imagen",
        _       => s.AuxAmount is null ? "sin coste por uso" : $"{Money(s.AuxAmount)} por {s.AuxUnit}",
    };

    private static string Money(decimal? value) =>
        value is null ? "—" : "$" + value.Value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// " (+20%)" cuando se puede calcular. Un porcentaje dice mas que la cifra suelta:
    /// pasar de $0,20 a $0,25 no parece nada hasta que se lee que es un 25% mas.
    /// </summary>
    private static string PercentSuffix(decimal? oldValue, decimal? newValue)
    {
        if (oldValue is not > 0m || newValue is null) return "";

        var pct = (newValue.Value - oldValue.Value) / oldValue.Value * 100m;
        var sign = pct >= 0 ? "+" : "";

        return $" ({sign}{pct.ToString("0.#", CultureInfo.InvariantCulture)}%)";
    }

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(value))]
    private static string? Truncate(string? value, int max) =>
        value is not null && value.Length > max ? value[..max] : value;

    private static ModelScanRunResponse ToDto(ModelScanRun r) =>
        new(r.Id, r.StartedAt, r.FinishedAt, r.Status, r.Trigger,
            r.ModelsScanned, r.ChangesDetected, r.NewModels, r.PriceChanges,
            r.ProvidersQueried, r.IsBaseline, r.Error);

    private static ModelScanChangeResponse ToDto(ModelCatalogChange c) =>
        new(c.Id, c.ScanId, c.ModelId, c.Provider, c.ChangeType,
            c.Field, c.OldValue, c.NewValue, c.Note, c.DetectedAt);
}
