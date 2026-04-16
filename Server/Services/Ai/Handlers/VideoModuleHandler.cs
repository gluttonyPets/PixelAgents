using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>Video generation from prompt + optional reference image.</summary>
public class VideoModuleHandler : IModuleHandler
{
    private readonly IAiProviderRegistry _registry;
    public string ModuleType => "Video";

    public VideoModuleHandler(IAiProviderRegistry registry) => _registry = registry;

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

        // Optional reference image
        var inputFiles = new List<byte[]>();
        var imageFiles = ctx.GetInputFiles("input_image");
        foreach (var fi in imageFiles)
        {
            var path = Path.Combine(ctx.MediaRoot, fi.FileName);
            if (File.Exists(path))
                inputFiles.Add(await File.ReadAllBytesAsync(path));
        }

        var aiContext = new AiExecutionContext
        {
            ModuleType = module.ModuleType,
            ModelName = module.ModelName,
            ApiKey = apiKey,
            Input = prompt,
            ProjectContext = ctx.Project.Context,
            Configuration = ctx.Config,
            InputFiles = inputFiles,
        };

        var result = await provider.ExecuteAsync(aiContext);
        if (!result.Success)
            return ModuleResult.Failed(result.Error ?? "Error en generacion de video");

        var producedFiles = new List<ProducedFile>();
        var outputFiles = new List<OutputFile>();

        if (result.FileOutput is { Length: > 0 })
        {
            var fileName = "output.mp4";
            producedFiles.Add(new ProducedFile
            {
                Data = result.FileOutput,
                FileName = fileName,
                ContentType = result.ContentType ?? "video/mp4",
            });
            outputFiles.Add(new OutputFile
            {
                FileName = fileName,
                ContentType = result.ContentType ?? "video/mp4",
                FileSize = result.FileOutput.Length,
            });
        }

        var output = OutputSchemaHelper.BuildVideoOutput(outputFiles, module.ModelName, result.Metadata);
        return ModuleResult.Completed(output, result.EstimatedCost, producedFiles);
    }
}
