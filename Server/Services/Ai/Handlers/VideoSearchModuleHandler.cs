using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>Video search (Pexels) module.</summary>
public class VideoSearchModuleHandler : IModuleHandler
{
    private readonly IAiProviderRegistry _registry;
    public string ModuleType => "VideoSearch";

    public VideoSearchModuleHandler(IAiProviderRegistry registry) => _registry = registry;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var query = ctx.GetInputText("input_query");
        if (string.IsNullOrWhiteSpace(query))
            return ModuleResult.Failed("Sin query de busqueda");

        var module = ctx.Node.AiModule;
        var provider = _registry.GetProvider(module.ProviderType);
        if (provider is null)
            return ModuleResult.Failed($"Proveedor '{module.ProviderType}' no disponible");

        var apiKey = module.ApiKey?.EncryptedKey;
        if (string.IsNullOrEmpty(apiKey))
            return ModuleResult.Failed("API Key no configurada");

        var aiContext = new AiExecutionContext
        {
            ModuleType = module.ModuleType,
            ModelName = module.ModelName,
            ApiKey = apiKey,
            Input = query,
            Configuration = ctx.Config,
        };

        var result = await provider.ExecuteAsync(aiContext);
        if (!result.Success)
            return ModuleResult.Failed(result.Error ?? "Error en busqueda de video");

        var producedFiles = new List<ProducedFile>();
        var outputFiles = new List<OutputFile>();

        if (result.FileOutput is { Length: > 0 })
        {
            var fileName = "video.mp4";
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
