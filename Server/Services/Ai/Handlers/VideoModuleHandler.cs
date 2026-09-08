using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Modulo de video: anima cada imagen de entrada y devuelve un clip por imagen.
///
/// Hace UNA LLAMADA POR IMAGEN, igual que <see cref="ImageModuleHandler"/> hace
/// una por escena, pero aqui las llamadas van EN PARALELO y no en serie. No es una
/// optimizacion opcional: un clip tarda minutos, el executor corta cada modulo a
/// los 10 minutos (<c>GraphPipelineExecutor</c>) y cinco clips en serie no caben en
/// ese presupuesto. En paralelo el modulo tarda lo que el clip mas lento.
///
/// El paralelismo esta limitado (<see cref="MaxParallelCalls"/>) porque los
/// proveedores de video tienen colas mucho mas estrechas que los de imagen: lanzar
/// veinte peticiones a la vez es la forma segura de comerse un 429 en todas.
///
/// El reparto del prompt es el mismo contrato que el multi-imagen: el texto comun
/// (estilo, camara, ritmo) se antepone a todos los clips y cada segmento describe
/// el movimiento de SU imagen. Si el texto no viene segmentado, todos los clips
/// comparten el mismo prompt de movimiento.
/// </summary>
public class VideoModuleHandler : IModuleHandler
{
    /// <summary>Puerto por el que entran las imagenes a animar.</summary>
    public const string ImagePort = "input_image";

    /// <summary>Puerto por el que entra la descripcion del movimiento.</summary>
    public const string PromptPort = "input_prompt";

    /// <summary>Llamadas simultaneas al proveedor de video.</summary>
    private const int MaxParallelCalls = 3;

    private readonly IAiProviderRegistry _registry;
    public string ModuleType => "Video";

    public VideoModuleHandler(IAiProviderRegistry registry) => _registry = registry;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var images = CollectInputImages(ctx);
        if (images.Count == 0)
            return ModuleResult.Failed(
                "Sin imagenes de entrada. Conecta un modulo de imagen (o de archivos) "
                + $"al puerto '{ImagePort}' de este modulo.");

        var module = ctx.Node.AiModule;
        var provider = _registry.GetProvider(module.ProviderType);
        if (provider is null)
            return ModuleResult.Failed($"Proveedor '{module.ProviderType}' no disponible");

        var apiKey = module.ApiKey?.EncryptedKey;
        if (string.IsNullOrEmpty(apiKey))
            return ModuleResult.Failed("API Key no configurada");

        var prompts = BuildPrompts(ctx, images.Count);

        await ctx.LogInfoAsync(
            $"Generando {images.Count} clip(s) con {module.ModelName} "
            + $"(hasta {MaxParallelCalls} en paralelo)");

        var results = new ClipResult[images.Count];
        using var gate = new SemaphoreSlim(MaxParallelCalls);

        var tasks = images.Select(async (image, index) =>
        {
            await gate.WaitAsync(ctx.CancellationToken);
            try
            {
                results[index] = await GenerateClipAsync(ctx, provider, apiKey, image, prompts[index], index);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        var producedFiles = new List<ProducedFile>();
        var outputFiles = new List<OutputFile>();
        var failures = new List<string>();
        var totalCost = 0m;
        var totalSeconds = 0d;

        for (var i = 0; i < results.Length; i++)
        {
            var clip = results[i];
            if (clip.Error is not null)
            {
                failures.Add($"clip {i + 1}: {clip.Error}");
                continue;
            }

            var fileName = $"clip_{i + 1}.mp4";
            producedFiles.Add(new ProducedFile
            {
                Data = clip.Data!,
                FileName = fileName,
                ContentType = clip.ContentType,
            });
            outputFiles.Add(new OutputFile
            {
                FileName = fileName,
                ContentType = clip.ContentType,
                FileSize = clip.Data!.Length,
            });

            totalCost += clip.Cost;
            totalSeconds += clip.Seconds;
        }

        // Ningun clip: no hay nada que entregar y el error es el de las llamadas.
        if (outputFiles.Count == 0)
            return ModuleResult.Failed("No se pudo generar ningun clip. " + string.Join(" | ", failures));

        // Algunos clips: se entregan los que salieron. El gasto ya esta hecho, asi que
        // tirarlos solo anadiria perdida a la perdida; lo que no puede pasar es que el
        // hueco sea silencioso, porque el montaje posterior saldria mas corto de lo
        // que el usuario pidio sin que nada lo dijera.
        if (failures.Count > 0)
            await ctx.LogWarningAsync(
                $"{failures.Count} de {images.Count} clips fallaron: {string.Join(" | ", failures)}");

        var output = OutputSchemaHelper.BuildVideoOutput(outputFiles, module.ModelName, new Dictionary<string, object>
        {
            ["requested"] = images.Count,
            ["failed"] = failures.Count,
            ["totalSeconds"] = totalSeconds,
        });

        if (failures.Count > 0)
            output.Metadata["failures"] = failures;

        return ModuleResult.Completed(output, totalCost, producedFiles);
    }

    private sealed record ClipResult(byte[]? Data, string ContentType, decimal Cost, double Seconds, string? Error)
    {
        public static ClipResult Ok(byte[] data, string contentType, decimal cost, double seconds) =>
            new(data, contentType, cost, seconds, null);

        public static ClipResult Failed(string error) => new(null, "video/mp4", 0m, 0, error);
    }

    private async Task<ClipResult> GenerateClipAsync(
        ModuleExecutionContext ctx,
        IAiProvider provider,
        string apiKey,
        byte[] image,
        string prompt,
        int index)
    {
        var module = ctx.Node.AiModule;

        var aiContext = new AiExecutionContext
        {
            ModuleType = module.ModuleType,
            ModelName = module.ModelName,
            ApiKey = apiKey,
            Input = prompt,
            ProjectContext = ctx.Project.Context,
            MandatoryRules = ctx.MandatoryRules,
            VariablesBlock = ctx.VariablesBlock,
            PastExecutionsLearning = ctx.PastExecutionsLearning,
            Configuration = new Dictionary<string, object>(ctx.Config, StringComparer.OrdinalIgnoreCase),
            InputFiles = [image],
            CancellationToken = ctx.CancellationToken,
        };

        try
        {
            var result = await provider.ExecuteAsync(aiContext);

            if (!result.Success || result.FileOutput is not { Length: > 0 })
                return ClipResult.Failed(result.Error ?? "el proveedor no devolvio video");

            var seconds = result.Metadata.TryGetValue("durationSeconds", out var raw)
                && double.TryParse(raw?.ToString(), out var parsed)
                ? parsed
                : 0d;

            await ctx.LogInfoAsync($"Clip {index + 1} listo ({result.FileOutput.Length / 1024} KB)");

            return ClipResult.Ok(
                result.FileOutput,
                result.ContentType ?? "video/mp4",
                result.EstimatedCost,
                seconds);
        }
        catch (OperationCanceledException)
        {
            throw; // La cancelacion es del executor, no un fallo del clip.
        }
        catch (Exception ex)
        {
            return ClipResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Imagenes a animar. Se mira primero el puerto propio y, si esta vacio,
    /// cualquier fichero de imagen que llegue por otro puerto: el usuario puede
    /// haber cableado el modulo de imagen al puerto de prompt, y fallar ahi por
    /// una cuestion de cableado seria gratuito.
    /// </summary>
    private static List<byte[]> CollectInputImages(ModuleExecutionContext ctx)
    {
        var files = ctx.GetInputFiles(ImagePort);

        if (files.Count == 0)
            files = ctx.InputsByPort
                .SelectMany(kv => kv.Value)
                .Where(d => d.Files is not null)
                .SelectMany(d => d.Files!)
                .Where(f => f.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
                .ToList();

        var images = new List<byte[]>(files.Count);
        foreach (var file in files)
        {
            var path = ctx.ResolveOutputFilePath(file);
            if (File.Exists(path))
                images.Add(File.ReadAllBytes(path));
        }

        return images;
    }

    /// <summary>
    /// Un prompt de movimiento por imagen: el contexto comun del nodo y de la
    /// entrada, mas el segmento que describe esa imagen concreta cuando el texto
    /// viene repartido.
    /// </summary>
    private static List<string> BuildPrompts(ModuleExecutionContext ctx, int count)
    {
        var configPrompt = ctx.ApplyVariables(ctx.GetConfig("videoPrompt", "")).Trim();
        var split = MultiImagePrompt.Split(ctx.GetAllInputTexts(PromptPort));

        var commonParts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(configPrompt)) commonParts.Add(configPrompt);
        if (!string.IsNullOrWhiteSpace(split.Common)) commonParts.Add(split.Common);
        var common = string.Join("\n\n", commonParts);

        var prompts = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var segment = i < split.Segments.Count ? split.Segments[i] : "";
            var parts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(segment)) parts.Add(segment.Trim());
            if (!string.IsNullOrWhiteSpace(common)) parts.Add(common);
            prompts.Add(string.Join("\n\n", parts));
        }

        return prompts;
    }
}
