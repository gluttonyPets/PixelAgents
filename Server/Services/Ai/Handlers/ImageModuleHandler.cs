using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Image generation module.
///
/// Cuando el modulo tiene una sola salida hace lo obvio: una llamada con el
/// prompt tal cual llega. Cuando tiene varias (n>1) NO pide n imagenes en una
/// llamada: "n" son muestras del mismo prompt y todas saldrian con el contenido
/// entero repetido. En su lugar reparte el texto de entrada en una parte por
/// imagen (ver <see cref="MultiImagePrompt"/>) y hace UNA LLAMADA POR PARTE,
/// cada una con n=1, de forma que la imagen i solo contiene la parte i.
/// </summary>
public class ImageModuleHandler : IModuleHandler
{
    private readonly IAiProviderRegistry _registry;
    private readonly IHttpClientFactory _httpFactory;
    public string ModuleType => "Image";

    public ImageModuleHandler(IAiProviderRegistry registry, IHttpClientFactory httpFactory)
    {
        _registry = registry;
        _httpFactory = httpFactory;
    }

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        // Combine the node's own imagePrompt with every upstream text so a
        // FileUpload + Text module fan-in doesn't silently drop either side.
        var configPrompt = ctx.GetConfig("imagePrompt", "");
        var upstreamTexts = ctx.GetAllInputTexts("input_prompt");

        var requested = MultiImagePrompt.ReadImageCount(ctx.Config);
        var split = MultiImagePrompt.Split(upstreamTexts);

        // Contexto comun a todas las imagenes: el prompt propio del nodo y todo
        // lo que llega sin segmentar (p. ej. el indice del modulo Directorio,
        // que entra por el mismo puerto y no pertenece a ninguna escena).
        var commonParts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(configPrompt)) commonParts.Add(configPrompt.Trim());
        if (!string.IsNullOrWhiteSpace(split.Common)) commonParts.Add(split.Common);
        var common = string.Join("\n\n", commonParts);

        var prompts = await BuildPromptsAsync(ctx, requested, common, split.Segments);
        var fanOut = prompts.Count > 1;

        if (prompts.All(string.IsNullOrWhiteSpace) && string.IsNullOrWhiteSpace(ctx.GetConfig("systemPrompt", "")))
            return ModuleResult.Failed("Sin prompt de entrada");

        var module = ctx.Node.AiModule;
        var provider = _registry.GetProvider(module.ProviderType);
        if (provider is null)
            return ModuleResult.Failed($"Proveedor '{module.ProviderType}' no disponible");

        var apiKey = module.ApiKey?.EncryptedKey;
        if (string.IsNullOrEmpty(apiKey))
            return ModuleResult.Failed("API Key no configurada");

        var inputFiles = new List<byte[]>();
        var inputFileInfos = ctx.GetInputFiles("input_prompt");
        foreach (var fi in inputFileInfos)
        {
            var bytes = await ctx.ReadOutputFileBytesAsync(fi);
            if (bytes is not null)
                inputFiles.Add(bytes);
        }

        // Cada llamada pide su propia imagen: en el reparto multi-imagen el
        // batch de n imagenes es justo lo que hay que evitar.
        var perCallN = fanOut ? 1 : requested;

        var allImages = new List<byte[]>();
        var payloads = new List<string>();
        var totalCost = 0m;
        string? contentType = null;
        string? revisedPrompt = null;
        var failures = new List<string>();
        // Una misma referencia citada en varias escenas se baja una sola vez.
        var referenceCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < prompts.Count; i++)
        {
            var prompt = prompts[i];
            var label = fanOut ? $"imagen {i + 1}/{prompts.Count}" : "imagen";

            // Referencias que el texto de esta imagen cita por URL. Es lo que permite dar
            // al modelo un indice de la biblioteca en vez de adjuntarle todo: elige
            // los ficheros que necesita y aqui se bajan solo esos. En el reparto
            // multi-imagen cada parte puede citar las suyas.
            var callFiles = new List<byte[]>(inputFiles);
            callFiles.AddRange(await FetchReferencedImagesAsync(ctx, prompt, referenceCache, label));

            var callConfig = new Dictionary<string, object>(ctx.Config, StringComparer.OrdinalIgnoreCase)
            {
                // Se escriben las dos claves porque no todos los proveedores leen
                // la misma primero (OpenAI/Grok miran "n", Gemini/Leonardo
                // "numberOfImages").
                ["n"] = perCallN,
                ["numberOfImages"] = perCallN,
            };

            var aiContext = new AiExecutionContext
            {
                ModuleType = module.ModuleType,
                ModelName = module.ModelName,
                ApiKey = apiKey,
                Input = prompt,
                ProjectContext = ctx.Project.Context,
                PreviousExecutionsSummary = ctx.PreviousSummaryContext,
                MandatoryRules = ctx.MandatoryRules,
                PastExecutionsLearning = ctx.PastExecutionsLearning,
                Configuration = callConfig,
                InputFiles = callFiles,
                CancellationToken = ctx.CancellationToken,
            };

            // Snapshot previo a la transformacion; los proveedores que construyen
            // el body exacto lo sustituyen mas abajo con result.SentPayload.
            StepPayloadBuilder.NormalizeContext(aiContext);
            var payload = StepPayloadBuilder.Serialize(aiContext, module.ProviderType);
            if (ctx.Node.StepExecution is not null)
                ctx.Node.StepExecution.InputData = payload;

            await LogCallAsync(ctx, module, label, prompt, callFiles, inputFileInfos, inputFiles.Count);

            var result = await provider.ExecuteAsync(aiContext);

            if (!string.IsNullOrEmpty(result.SentPayload))
                payload = result.SentPayload;
            payloads.Add(payload);

            if (!string.IsNullOrEmpty(result.TruncationWarning))
                await ctx.LogWarningAsync(result.TruncationWarning);

            if (!result.Success)
            {
                var error = result.Error ?? "Error en generacion de imagen";
                await ctx.LogWarningAsync($"[Image] {module.ProviderType} devolvio error en {label}: {error}");
                failures.Add($"{label}: {error}");
                continue;
            }

            var callImages = new List<byte[]>();
            if (result.FileOutput is { Length: > 0 })
                callImages.Add(result.FileOutput);
            if (result.AdditionalFiles is { Count: > 0 })
                callImages.AddRange(result.AdditionalFiles.Where(b => b.Length > 0));

            if (callImages.Count == 0)
            {
                await ctx.LogWarningAsync($"[Image] El proveedor no devolvio imagenes en {label}.");
                failures.Add($"{label}: el proveedor no devolvio imagenes");
                continue;
            }

            allImages.AddRange(callImages);
            totalCost += result.EstimatedCost;
            contentType ??= result.ContentType;
            revisedPrompt ??= result.Metadata?.GetValueOrDefault("revisedPrompt")?.ToString();

            await ctx.LogInfoAsync(
                $"[Image] {module.ProviderType}/{module.ModelName}: {label} lista, " +
                $"{callImages.Count} imagen(es) ({FormatBytes(callImages.Sum(b => (long)b.Length))}).");
        }

        // La traza guarda las N llamadas: con una sola se mantiene el JSON de
        // siempre para no romper la vista de detalle de ejecucion.
        if (ctx.Node.StepExecution is not null && payloads.Count > 0)
            ctx.Node.StepExecution.InputData = payloads.Count == 1
                ? payloads[0]
                : $"[{string.Join(",", payloads)}]";

        if (allImages.Count == 0)
            return ModuleResult.Failed(failures.Count > 0
                ? string.Join(" | ", failures)
                : "El proveedor no devolvio imagenes");

        if (failures.Count > 0)
            await ctx.LogWarningAsync(
                $"[Image] {failures.Count} de {prompts.Count} llamada(s) fallaron; " +
                $"se continua con {allImages.Count} imagen(es). Los puertos sin imagen no propagan datos.");

        contentType = string.IsNullOrEmpty(contentType) ? "image/png" : contentType;

        var ext = contentType switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".jpg"
        };

        var producedFiles = new List<ProducedFile>();
        var outputFiles = new List<OutputFile>();

        for (int i = 0; i < allImages.Count; i++)
        {
            var fileName = allImages.Count > 1 ? $"image_{i + 1}{ext}" : $"output{ext}";
            producedFiles.Add(new ProducedFile
            {
                Data = allImages[i],
                FileName = fileName,
                ContentType = contentType,
            });
            outputFiles.Add(new OutputFile
            {
                FileName = fileName,
                ContentType = contentType,
                FileSize = allImages[i].Length,
                RevisedPrompt = revisedPrompt,
            });
        }

        var output = OutputSchemaHelper.BuildImageOutput(outputFiles, module.ModelName);
        output.Metadata["count"] = allImages.Count;

        return ModuleResult.Completed(output, totalCost, producedFiles);
    }

    /// <summary>
    /// Decide que prompts se envian: uno por escena cuando el texto llega
    /// segmentado, o uno solo en cualquier otro caso. Deja dicho en el log por
    /// que, porque es la diferencia entre N imagenes distintas y N copias.
    /// </summary>
    private static async Task<List<string>> BuildPromptsAsync(
        ModuleExecutionContext ctx, int requested, string common, List<string> segments)
    {
        if (requested <= 1)
        {
            if (segments.Count > 1)
                await ctx.LogWarningAsync(
                    $"[Image] El texto de entrada trae {segments.Count} partes pero el modulo esta configurado " +
                    "para 1 imagen: se generan todas juntas en una sola. Sube el numero de imagenes del modulo.");
            return [Join(common, segments)];
        }

        if (segments.Count < 2)
        {
            await ctx.LogWarningAsync(
                $"[Image] El modulo pide {requested} imagenes pero el texto de entrada no viene separado en partes " +
                $"({MultiImagePrompt.BuildMarker(1)}, {MultiImagePrompt.BuildMarker(2)}, ...), asi que no hay nada que repartir: " +
                $"se piden las {requested} al proveedor con el MISMO prompt y saldran repetidas. " +
                "Conecta un modulo de texto delante para que planifique un prompt por imagen.");
            return [Join(common, segments)];
        }

        if (segments.Count != requested)
            await ctx.LogWarningAsync(
                $"[Image] El texto trae {segments.Count} parte(s) y el modulo tiene {requested} salida(s). " +
                $"Se generan {Math.Min(segments.Count, requested)}.");

        var effective = Math.Min(segments.Count, requested);
        await ctx.LogInfoAsync(
            $"[Image] Reparto multi-imagen: {effective} llamada(s) independientes, una por parte del prompt.");

        return segments.Take(effective).Select(s => Join(common, [s])).ToList();
    }

    private static string Join(string common, List<string> parts)
    {
        var pieces = new List<string>(parts.Count + 1);
        if (!string.IsNullOrWhiteSpace(common)) pieces.Add(common);
        pieces.AddRange(parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return string.Join("\n\n", pieces);
    }

    /// <summary>
    /// Trazabilidad: deja constancia de que la imagen viaja al proveedor.
    /// Sin esto solo se ve "inputFilesCount" en el JSON de la UI y es facil
    /// dudar si los bytes realmente se enviaron.
    /// </summary>
    private static Task LogCallAsync(
        ModuleExecutionContext ctx, AiModule module, string label, string prompt,
        List<byte[]> callFiles, List<OutputFile> portFileInfos, int portFileCount)
    {
        if (callFiles.Count == 0)
            return ctx.LogInfoAsync(
                $"[Image] {module.ProviderType}/{module.ModelName}: {label} sin archivos de entrada " +
                $"(solo prompt, {prompt.Length} chars).");

        var perFile = string.Join(", ", portFileInfos
            .Take(portFileCount)
            .Select((f, i) => $"{f.FileName} ({f.ContentType}, {FormatBytes(callFiles[i].Length)})"));
        var extra = callFiles.Count - portFileCount;
        if (extra > 0)
            perFile = string.IsNullOrEmpty(perFile)
                ? $"{extra} referencia(s) del directorio"
                : $"{perFile}, +{extra} referencia(s) del directorio";

        return ctx.LogInfoAsync(
            $"[Image] {module.ProviderType}/{module.ModelName}: {label} enviando {callFiles.Count} archivo(s) " +
            $"[{perFile}], total {FormatBytes(callFiles.Sum(b => (long)b.Length))}, prompt {prompt.Length} chars.");
    }

    /// <summary>
    /// Baja las imagenes del directorio publico que el prompt cite por URL. Se
    /// limita a las de este servidor y a un maximo configurable: si llega el
    /// indice entero (por conectar el Directorio directo a este nodo) no tiene
    /// sentido descargar la biblioteca completa.
    /// </summary>
    private async Task<List<byte[]>> FetchReferencedImagesAsync(
        ModuleExecutionContext ctx, string prompt, Dictionary<string, byte[]> cache, string label)
    {
        var max = ctx.GetConfigInt("maxReferenceImages", ReferenceImageFetcher.DefaultMaxImages);
        var urls = ReferenceImageFetcher.ExtractDirectoryUrls(prompt, ctx.PublicBaseUrl, max);
        if (urls.Count == 0) return [];

        var total = ReferenceImageFetcher.CountDirectoryUrls(prompt, ctx.PublicBaseUrl);
        if (total > urls.Count)
        {
            await ctx.LogWarningAsync(
                $"[Image] {label}: el texto cita {total} imagenes del directorio y solo se usan las {urls.Count} primeras. "
                + "Conecta el Directorio a un modulo de texto que elija, en vez de a este nodo, "
                + "o sube el maximo de imagenes de referencia.");
        }

        var pending = urls.Where(u => !cache.ContainsKey(u)).ToList();
        if (pending.Count > 0)
        {
            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(2);
            foreach (var f in await ReferenceImageFetcher.DownloadAsync(http, pending, ctx.CancellationToken))
                cache[f.Url] = f.Data;
        }

        var fetched = urls.Where(cache.ContainsKey).Select(u => (Url: u, Data: cache[u])).ToList();

        if (fetched.Count == 0)
        {
            await ctx.LogWarningAsync(
                $"[Image] {label}: ninguna de las {urls.Count} referencia(s) del directorio se pudo descargar; "
                + "se genera solo con el texto.");
            return [];
        }

        var names = string.Join(", ", fetched.Select(f =>
            $"{ReferenceImageFetcher.FileNameOf(f.Url)} ({FormatBytes(f.Data.Length)})"));
        await ctx.LogInfoAsync($"[Image] {label}: {fetched.Count} imagen(es) de referencia del directorio: {names}.");

        return fetched.Select(f => f.Data).ToList();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }
}
