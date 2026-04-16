using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Image generation module. Can generate N images from a prompt.
/// Supports image-to-image editing when connected to file inputs.
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
            return ModuleResult.Failed("Sin prompt de entrada");

        var module = ctx.Node.AiModule;
        var provider = _registry.GetProvider(module.ProviderType);
        if (provider is null)
            return ModuleResult.Failed($"Proveedor '{module.ProviderType}' no disponible");

        var apiKey = module.ApiKey?.EncryptedKey;
        if (string.IsNullOrEmpty(apiKey))
            return ModuleResult.Failed("API Key no configurada");

        // Determine image count
        var imageCount = 1;
        if (ctx.Config.TryGetValue("n", out var nVal))
        {
            if (nVal is int n) imageCount = n;
            else if (nVal is System.Text.Json.JsonElement je && je.TryGetInt32(out var jn)) imageCount = jn;
        }
        if (ctx.Config.TryGetValue("numberOfImages", out var niVal))
        {
            if (niVal is int ni) imageCount = ni;
            else if (niVal is System.Text.Json.JsonElement je && je.TryGetInt32(out var jni)) imageCount = jni;
        }

        // Check for input files (image-to-image)
        var inputFiles = new List<byte[]>();
        var fileInputs = ctx.GetInputFiles("input_prompt");
        if (fileInputs.Count > 0)
        {
            // Load image files from workspace
            foreach (var fi in fileInputs)
            {
                var bytes = await ctx.ReadOutputFileBytesAsync(fi);
                if (bytes is not null)
                    inputFiles.Add(bytes);
            }
        }

        var producedFiles = new List<ProducedFile>();
        var outputFiles = new List<OutputFile>();
        var totalCost = 0m;

        for (int i = 0; i < imageCount; i++)
        {
            var aiContext = new AiExecutionContext
            {
                ModuleType = module.ModuleType,
                ModelName = module.ModelName,
                ApiKey = apiKey,
                Input = prompt,
                ProjectContext = ctx.Project.Context,
                PreviousExecutionsSummary = ctx.PreviousSummaryContext,
                Configuration = ctx.Config,
                InputFiles = inputFiles,
            };

            var result = await provider.ExecuteAsync(aiContext);
            totalCost += result.EstimatedCost;

            if (!result.Success)
                return ModuleResult.Failed(result.Error ?? "Error en generacion de imagen");

            if (result.FileOutput is { Length: > 0 })
            {
                var ext = result.ContentType switch
                {
                    "image/png" => ".png",
                    "image/webp" => ".webp",
                    _ => ".jpg"
                };
                var fileName = imageCount > 1 ? $"image_{i + 1}{ext}" : $"output{ext}";

                producedFiles.Add(new ProducedFile
                {
                    Data = result.FileOutput,
                    FileName = fileName,
                    ContentType = result.ContentType ?? "image/png",
                });

                outputFiles.Add(new OutputFile
                {
                    FileName = fileName,
                    ContentType = result.ContentType ?? "image/png",
                    FileSize = result.FileOutput.Length,
                    RevisedPrompt = result.Metadata?.GetValueOrDefault("revisedPrompt")?.ToString(),
                });
            }
        }

        var output = OutputSchemaHelper.BuildImageOutput(outputFiles, module.ModelName);
        output.Metadata["count"] = imageCount;

        return ModuleResult.Completed(output, totalCost, producedFiles);
    }
}
