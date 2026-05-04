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
        var prompt = ctx.GetInputText("input_prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = ctx.GetConfig("imagePrompt", "");
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
        foreach (var fi in ctx.GetInputFiles("input_prompt"))
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

        StepPayloadBuilder.RecordSentPayload(ctx, aiContext, module.ProviderType);

        var result = await provider.ExecuteAsync(aiContext);
        if (!result.Success)
            return ModuleResult.Failed(result.Error ?? "Error en generacion de imagen");

        var allImages = new List<byte[]>();
        if (result.FileOutput is { Length: > 0 })
            allImages.Add(result.FileOutput);
        if (result.AdditionalFiles is { Count: > 0 })
            allImages.AddRange(result.AdditionalFiles.Where(b => b.Length > 0));

        if (allImages.Count == 0)
            return ModuleResult.Failed("El proveedor no devolvio imagenes");

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
}
