using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>Text-to-Speech module.</summary>
public class AudioModuleHandler : IModuleHandler
{
    private readonly IAiProviderRegistry _registry;
    public string ModuleType => "Audio";

    public AudioModuleHandler(IAiProviderRegistry registry) => _registry = registry;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var text = ctx.GetInputText("input_text");
        if (string.IsNullOrWhiteSpace(text))
            return ModuleResult.Failed("Sin texto de entrada para TTS");

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
            Input = text,
            Configuration = ctx.Config,
        };

        var result = await provider.ExecuteAsync(aiContext);
        if (!result.Success)
            return ModuleResult.Failed(result.Error ?? "Error en TTS");

        var producedFiles = new List<ProducedFile>();
        var outputFiles = new List<OutputFile>();

        if (result.FileOutput is { Length: > 0 })
        {
            var fileName = "output.mp3";
            producedFiles.Add(new ProducedFile
            {
                Data = result.FileOutput,
                FileName = fileName,
                ContentType = result.ContentType ?? "audio/mpeg",
            });
            outputFiles.Add(new OutputFile
            {
                FileName = fileName,
                ContentType = result.ContentType ?? "audio/mpeg",
                FileSize = result.FileOutput.Length,
            });
        }

        var output = OutputSchemaHelper.BuildAudioOutput(outputFiles, module.ModelName);
        foreach (var (key, value) in result.Metadata)
            output.Metadata[key] = value;
        return ModuleResult.Completed(output, result.EstimatedCost, producedFiles);
    }
}
