using System.Text.Json;
using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Image generation module. Delegates to the provider so it can request N
/// non-repeated images in a single API call (natively, via the model's
/// `n`/`num_images` parameter) instead of looping with identical prompts.
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

        // Input files for image-to-image editing
        var inputFiles = new List<byte[]>();
        foreach (var fi in ctx.GetInputFiles("input_prompt"))
        {
            var bytes = await ctx.ReadOutputFileBytesAsync(fi);
            if (bytes is not null)
                inputFiles.Add(bytes);
        }

        // Single call — the provider reads `n`/`numberOfImages` from ctx.Config and
        // asks the API for all images at once so the model knows to vary them.
        // When N>1 we prepend a rule so the model treats the composite prompt as
        // N distinct parts (one per image) instead of producing N variants that
        // all try to render every part together.
        var n = ReadImageCount(ctx.Config);
        var finalPrompt = n > 1
            ? OutputSchemaHelper.GetMultiImageDisaggregationRule(n) + "\n\n" + prompt
            : prompt;

        var aiContext = new AiExecutionContext
        {
            ModuleType = module.ModuleType,
            ModelName = module.ModelName,
            ApiKey = apiKey,
            Input = finalPrompt,
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

        var contentType = result.ContentType ?? "image/png";
        var ext = contentType switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".jpg"
        };

        var producedFiles = new List<ProducedFile>();
        var outputFiles = new List<OutputFile>();
        var revisedPrompt = result.Metadata?.GetValueOrDefault("revisedPrompt")?.ToString();

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

    /// <summary>
    /// Mirrors OpenAiProvider.ReadImageCount: reads `n` or `numberOfImages`
    /// from the merged module config, tolerating int/long/double/JsonElement/string.
    /// </summary>
    private static int ReadImageCount(IDictionary<string, object> config)
    {
        int Parse(object? v) => v switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            JsonElement je when je.TryGetInt32(out var ji) => ji,
            string s when int.TryParse(s, out var sp) => sp,
            _ => 1,
        };
        if (config.TryGetValue("n", out var nv)) return Math.Max(1, Parse(nv));
        if (config.TryGetValue("numberOfImages", out var niv)) return Math.Max(1, Parse(niv));
        return 1;
    }
}
