using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>Text embeddings generation module.</summary>
public class EmbeddingsModuleHandler : IModuleHandler
{
    private readonly IAiProviderRegistry _registry;
    public string ModuleType => "Embeddings";

    public EmbeddingsModuleHandler(IAiProviderRegistry registry) => _registry = registry;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var text = ctx.GetInputText("input_text");
        if (string.IsNullOrWhiteSpace(text))
            return ModuleResult.Failed("Sin texto de entrada para embeddings");

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
            return ModuleResult.Failed(result.Error ?? "Error en embeddings");

        var output = new StepOutput
        {
            Type = "text",
            Content = result.TextOutput ?? "",
        };

        return ModuleResult.Completed(output, result.EstimatedCost);
    }
}
