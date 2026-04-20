using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Text generation module. Calls AI provider with structured JSON output.
/// Input: input_prompt (text) -> Output: output_text (text with items)
/// </summary>
public class TextModuleHandler : IModuleHandler
{
    private readonly IAiProviderRegistry _registry;
    public string ModuleType => "Text";

    public TextModuleHandler(IAiProviderRegistry registry) => _registry = registry;

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
        };

        StepPayloadBuilder.RecordSentPayload(ctx, aiContext, module.ProviderType);

        var result = await provider.ExecuteAsync(aiContext);
        if (!result.Success)
            return ModuleResult.Failed(result.Error ?? "Error en generacion de texto");

        var rawText = result.TextOutput ?? "";
        var stepOutput = new StepOutput
        {
            Type = "text",
            Content = rawText,
            Metadata = result.Metadata ?? new(),
        };

        var producedFiles = new List<ProducedFile>();
        if (!string.IsNullOrEmpty(rawText))
        {
            producedFiles.Add(new ProducedFile
            {
                Data = System.Text.Encoding.UTF8.GetBytes(rawText),
                FileName = "output.txt",
                ContentType = "text/plain",
            });
        }

        return ModuleResult.Completed(stepOutput, result.EstimatedCost, producedFiles);
    }
}
