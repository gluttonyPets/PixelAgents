using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Image generation module. Forwards the upstream payload as-is to the provider
/// and lets the model interpret it. The provider reads `n`/`numberOfImages`
/// from the module config to request multiple images in a single call.
/// </summary>
public class ImageModuleHandler : IModuleHandler
{
    private readonly IAiProviderRegistry _registry;
    public string ModuleType => "Image";

    public ImageModuleHandler(IAiProviderRegistry registry) => _registry = registry;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        // Combine the node's own imagePrompt with every upstream text so a
        // FileUpload + Text module fan-in doesn't silently drop either side.
        var configPrompt = ctx.GetConfig("imagePrompt", "");
        var upstreamPrompt = ctx.GetInputText("input_prompt");
        var promptParts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(configPrompt)) promptParts.Add(configPrompt);
        if (!string.IsNullOrWhiteSpace(upstreamPrompt)) promptParts.Add(upstreamPrompt);
        var prompt = string.Join("\n\n", promptParts);
        if (string.IsNullOrWhiteSpace(prompt) && string.IsNullOrWhiteSpace(ctx.GetConfig("systemPrompt", "")))
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

        var aiContext = new AiExecutionContext
        {
            ModuleType = module.ModuleType,
            ModelName = module.ModelName,
            ApiKey = apiKey,
            Input = prompt,
            ProjectContext = ctx.Project.Context,
            PreviousExecutionsSummary = ctx.PreviousSummaryContext,
            MandatoryRules = ctx.MandatoryRules,
            Configuration = ctx.Config,
            InputFiles = inputFiles,
        };

        // Record a pre-transform snapshot; providers that build the exact API body
        // will overwrite this below via result.SentPayload.
        StepPayloadBuilder.RecordSentPayload(ctx, aiContext, module.ProviderType);

        // Trazabilidad: deja constancia de que la imagen viaja al proveedor.
        // Sin esto solo se ve "inputFilesCount" en el JSON de la UI y es facil
        // dudar si los bytes realmente se enviaron.
        var totalBytes = inputFiles.Sum(b => (long)b.Length);
        if (inputFiles.Count == 0)
        {
            await ctx.LogInfoAsync(
                $"[Image] {module.ProviderType}/{module.ModelName}: sin archivos de entrada (solo prompt, {prompt.Length} chars).");
        }
        else
        {
            var perFile = string.Join(", ", inputFileInfos
                .Take(inputFiles.Count)
                .Select((f, i) => $"{f.FileName} ({f.ContentType}, {FormatBytes(inputFiles[i].Length)})"));
            await ctx.LogInfoAsync(
                $"[Image] {module.ProviderType}/{module.ModelName}: enviando {inputFiles.Count} archivo(s) " +
                $"[{perFile}], total {FormatBytes(totalBytes)}, prompt {prompt.Length} chars.");
        }

        var result = await provider.ExecuteAsync(aiContext);

        // Overwrite the pre-transform snapshot with the exact payload the provider sent.
        if (!string.IsNullOrEmpty(result.SentPayload) && ctx.Node.StepExecution is not null)
            ctx.Node.StepExecution.InputData = result.SentPayload;

        if (!string.IsNullOrEmpty(result.TruncationWarning))
            await ctx.LogWarningAsync(result.TruncationWarning);

        if (!result.Success)
        {
            await ctx.LogInfoAsync($"[Image] {module.ProviderType} devolvio error: {result.Error}");
            return ModuleResult.Failed(result.Error ?? "Error en generacion de imagen");
        }

        var allImages = new List<byte[]>();
        if (result.FileOutput is { Length: > 0 })
            allImages.Add(result.FileOutput);
        if (result.AdditionalFiles is { Count: > 0 })
            allImages.AddRange(result.AdditionalFiles.Where(b => b.Length > 0));

        if (allImages.Count == 0)
            return ModuleResult.Failed("El proveedor no devolvio imagenes");

        var totalOutBytes = allImages.Sum(b => (long)b.Length);
        await ctx.LogInfoAsync(
            $"[Image] {module.ProviderType}/{module.ModelName}: respuesta recibida, " +
            $"{allImages.Count} imagen(es) ({FormatBytes(totalOutBytes)}).");

        var contentType = string.IsNullOrEmpty(result.ContentType) ? "image/png" : result.ContentType;
        var revisedPrompt = result.Metadata?.GetValueOrDefault("revisedPrompt")?.ToString();

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

        return ModuleResult.Completed(output, result.EstimatedCost, producedFiles);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }
}
