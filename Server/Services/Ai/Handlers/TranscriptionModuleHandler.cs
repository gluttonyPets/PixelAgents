using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>Speech-to-Text module.</summary>
public class TranscriptionModuleHandler : IModuleHandler
{
    private readonly IAiProviderRegistry _registry;
    public string ModuleType => "Transcription";

    public TranscriptionModuleHandler(IAiProviderRegistry registry) => _registry = registry;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var module = ctx.Node.AiModule;
        var provider = _registry.GetProvider(module.ProviderType);
        if (provider is null)
            return ModuleResult.Failed($"Proveedor '{module.ProviderType}' no disponible");

        var apiKey = module.ApiKey?.EncryptedKey;
        if (string.IsNullOrEmpty(apiKey))
            return ModuleResult.Failed("API Key no configurada");

        // Load audio file from input
        var inputFiles = new List<byte[]>();
        var audioFiles = ctx.GetInputFiles("input_audio");
        foreach (var fi in audioFiles)
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
            Input = "",
            Configuration = ctx.Config,
            InputFiles = inputFiles,
        };

        var result = await provider.ExecuteAsync(aiContext);
        if (!result.Success)
            return ModuleResult.Failed(result.Error ?? "Error en transcripcion");

        var output = new StepOutput
        {
            Type = "text",
            Content = result.TextOutput ?? "",
        };

        return ModuleResult.Completed(output, result.EstimatedCost);
    }
}
