using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Server.Data;
using Server.Models;
using Server.Services;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Ejecuta un proyecto completo (referenciado por <see cref="ProjectModule.SubProjectId"/>)
/// como un unico modulo dentro del pipeline padre. La entrada que llega al nodo se
/// usa como <c>UserInput</c> del proyecto insertado (equivale a su modulo de Inicio);
/// se ejecutan todos sus modulos y la salida de su nodo terminal vuelve al pipeline
/// padre como salida de este nodo.
///
/// Es una ejecucion HIJA aislada: crea su propio scope, su propio executor y su propio
/// DbContext de tenant. Los archivos producidos por el proyecto insertado se copian a la
/// ejecucion padre para que los modulos siguientes puedan resolverlos con normalidad.
/// </summary>
public class SubProjectModuleHandler : IModuleHandler
{
    // Guarda de profundidad frente a cadenas de sub-proyectos muy anidadas. La
    // recursion directa/indirecta se bloquea al insertar el nodo; esto es una
    // defensa en tiempo de ejecucion por si la definicion cambia despues.
    private const int MaxDepth = 5;
    private static readonly AsyncLocal<int> Depth = new();

    private static readonly JsonSerializerOptions JsonOptions = AiJson.Compact;

    // Tipos de modulo que pausan la ejecucion esperando intervencion humana.
    // En esta version no se soportan dentro de un sub-proyecto porque la
    // ejecucion hija es sincrona respecto al nodo padre.
    private static readonly HashSet<string> PausingTypes =
        new(StringComparer.OrdinalIgnoreCase) { "Interaction", "Checkpoint" };

    private readonly IServiceProvider _serviceProvider;
    private readonly ITenantDbContextFactory _tenantFactory;

    public SubProjectModuleHandler(
        IServiceProvider serviceProvider,
        ITenantDbContextFactory tenantFactory)
    {
        _serviceProvider = serviceProvider;
        _tenantFactory = tenantFactory;
    }

    public string ModuleType => "SubProject";

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var subProjectId = ctx.Node.ProjectModule.SubProjectId;
        if (subProjectId is null || subProjectId == Guid.Empty)
            return ModuleResult.Failed("El nodo de sub-proyecto no tiene un proyecto asignado. Vuelve a seleccionarlo.");

        if (Depth.Value >= MaxDepth)
            return ModuleResult.Failed($"Cadena de sub-proyectos demasiado profunda (limite {MaxDepth}). Revisa que no haya referencias ciclicas.");

        var input = CollectInputText(ctx);

        await using var childDb = _tenantFactory.Create(ctx.TenantDbName);

        // Validaciones sobre la definicion actual del proyecto insertado.
        var childProject = await childDb.Projects
            .Include(p => p.ProjectModules)
                .ThenInclude(pm => pm.AiModule)
            .FirstOrDefaultAsync(p => p.Id == subProjectId, ctx.CancellationToken);

        if (childProject is null)
            return ModuleResult.Failed("El proyecto insertado ya no existe. Elimina el nodo o selecciona otro proyecto.");

        var activeModules = childProject.ProjectModules.Where(pm => pm.IsActive).ToList();
        if (activeModules.Count == 0)
            return ModuleResult.Failed($"El proyecto insertado '{childProject.Name}' no tiene modulos activos.");

        var pausing = activeModules
            .Where(pm => PausingTypes.Contains(pm.AiModule.ModuleType))
            .Select(pm => pm.AiModule.ModuleType)
            .Distinct()
            .ToList();
        if (pausing.Count > 0)
            return ModuleResult.Failed(
                $"El proyecto insertado '{childProject.Name}' contiene modulos interactivos ({string.Join(", ", pausing)}) " +
                "que pausan la ejecucion. Los sub-proyectos no admiten pausas humanas en esta version.");

        await ctx.LogInfoAsync($"Ejecutando sub-proyecto '{childProject.Name}' con {activeModules.Count} modulo(s)...");

        ProjectExecution childExecution;
        Depth.Value++;
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var executor = scope.ServiceProvider.GetRequiredService<IPipelineExecutor>();
            // useHistory:false — el "no repetir tematicas" es responsabilidad del
            // pipeline padre; la ejecucion hija no debe arrastrar su propio historial.
            childExecution = await executor.ExecuteAsync(
                subProjectId.Value, input, childDb, ctx.TenantDbName, ctx.CancellationToken, useHistory: false);
        }
        finally
        {
            Depth.Value--;
        }

        if (!string.Equals(childExecution.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            return ModuleResult.Failed(
                $"El sub-proyecto '{childProject.Name}' termino con estado '{childExecution.Status}' en vez de completarse.");

        return await BuildResultFromChildAsync(ctx, childProject, childExecution, childDb);
    }

    /// <summary>
    /// Junta todo el texto que llega por los puertos de entrada del nodo. Si no
    /// llega nada, cae al UserInput global del pipeline padre.
    /// </summary>
    private static string CollectInputText(ModuleExecutionContext ctx)
    {
        var texts = ctx.InputsByPort.Values
            .SelectMany(list => list)
            .Select(d => d.TextContent)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Cast<string>()
            .ToList();

        if (texts.Count > 0)
            return string.Join("\n\n", texts);

        return ctx.Graph.UserInput ?? "";
    }

    /// <summary>
    /// Toma la salida de los nodos terminales del proyecto insertado (los que no
    /// tienen conexiones salientes) y la combina en un unico <see cref="StepOutput"/>.
    /// Los archivos se leen de disco y se devuelven como <see cref="ProducedFile"/>
    /// para que el executor padre los persista dentro de esta ejecucion.
    /// </summary>
    private async Task<ModuleResult> BuildResultFromChildAsync(
        ModuleExecutionContext ctx,
        Project childProject,
        ProjectExecution childExecution,
        UserDbContext childDb)
    {
        var ct = ctx.CancellationToken;

        var connections = await childDb.ModuleConnections
            .Where(c => c.ProjectId == childProject.Id)
            .ToListAsync(ct);
        var modulesWithOutgoing = connections.Select(c => c.FromModuleId).ToHashSet();

        // Terminales = modulos activos sin conexion saliente, excluyendo Start
        // (Start solo seria terminal en un proyecto vacio, que ya se descarto).
        var terminalIds = childProject.ProjectModules
            .Where(pm => pm.IsActive
                && pm.AiModule.ModuleType != "Start"
                && !modulesWithOutgoing.Contains(pm.Id))
            .Select(pm => pm.Id)
            .ToHashSet();

        var steps = await childDb.StepExecutions
            .Include(s => s.Files)
            .Where(s => s.ExecutionId == childExecution.Id && s.Status == "Completed")
            .ToListAsync(ct);

        // Prioriza los pasos terminales; si por alguna razon no hay ninguno,
        // usa el ultimo paso completado como salida.
        var terminalSteps = steps.Where(s => terminalIds.Contains(s.ProjectModuleId)).ToList();
        if (terminalSteps.Count == 0 && steps.Count > 0)
            terminalSteps = new List<StepExecution> { steps.OrderBy(s => s.CompletedAt).Last() };

        var childWorkspace = ResolveChildWorkspace(ctx.MediaRoot, childExecution.WorkspacePath);

        var contents = new List<string>();
        var items = new List<OutputItem>();
        var outputFiles = new List<OutputFile>();
        var producedFiles = new List<ProducedFile>();
        string? title = null;

        foreach (var step in terminalSteps)
        {
            if (string.IsNullOrWhiteSpace(step.OutputData))
                continue;

            StepOutput? childOutput;
            try { childOutput = JsonSerializer.Deserialize<StepOutput>(step.OutputData, JsonOptions); }
            catch { continue; }
            if (childOutput is null)
                continue;

            if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(childOutput.Title))
                title = childOutput.Title;
            if (!string.IsNullOrWhiteSpace(childOutput.Content))
                contents.Add(childOutput.Content!);
            items.AddRange(childOutput.Items);

            foreach (var file in childOutput.Files)
            {
                var bytes = await ReadChildFileBytesAsync(childWorkspace, step, file, ct);
                if (bytes is null)
                    continue;

                var fileName = Path.GetFileName(file.FileName);
                producedFiles.Add(new ProducedFile
                {
                    Data = bytes,
                    FileName = fileName,
                    ContentType = file.ContentType,
                });
                // FileId vacio + mismo FileName -> el executor padre enlaza el
                // ExecutionFile recien creado con esta entrada de salida.
                outputFiles.Add(new OutputFile
                {
                    FileId = Guid.Empty,
                    FileName = fileName,
                    ContentType = file.ContentType,
                    FileSize = bytes.LongLength,
                });
            }
        }

        var combinedText = string.Join("\n\n", contents);
        var output = new StepOutput
        {
            Type = producedFiles.Count > 0 && string.IsNullOrWhiteSpace(combinedText) ? "file" : "text",
            Title = title,
            Content = combinedText,
            Summary = $"Sub-proyecto '{childProject.Name}' completado ({terminalSteps.Count} salida(s), {producedFiles.Count} archivo(s))",
            Items = items,
            Files = outputFiles,
            Metadata = new Dictionary<string, object>
            {
                ["subProjectId"] = childProject.Id.ToString(),
                ["subProjectName"] = childProject.Name,
                ["childExecutionId"] = childExecution.Id.ToString(),
            },
        };

        await ctx.LogInfoAsync(
            $"Sub-proyecto '{childProject.Name}' completado. Salida: {combinedText.Length} car., {producedFiles.Count} archivo(s).");

        return ModuleResult.Completed(output, childExecution.TotalEstimatedCost, producedFiles);
    }

    private static string ResolveChildWorkspace(string mediaRoot, string workspacePath) =>
        Path.IsPathRooted(workspacePath) ? workspacePath : Path.Combine(mediaRoot, workspacePath);

    private static async Task<byte[]?> ReadChildFileBytesAsync(
        string childWorkspace, StepExecution step, OutputFile file, CancellationToken ct)
    {
        // Ruta relativa registrada en ExecutionFile (respecto al workspace hijo).
        var execFile = step.Files.FirstOrDefault(f => f.Id == file.FileId)
            ?? step.Files.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f.FileName), Path.GetFileName(file.FileName), StringComparison.OrdinalIgnoreCase));

        var candidates = new List<string>();
        if (execFile is not null)
            candidates.Add(Path.Combine(childWorkspace, execFile.FilePath));
        candidates.Add(Path.Combine(childWorkspace, file.FileName));

        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
            return null;

        return await File.ReadAllBytesAsync(path, ct);
    }
}
