using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Coordinator: aggregates data from multiple input ports and sends to AI.
/// Each input_N port receives data from a connected module.
/// </summary>
public class CoordinatorModuleHandler : IModuleHandler
{
    private readonly IAiProviderRegistry _registry;
    public string ModuleType => "Coordinator";

    public CoordinatorModuleHandler(IAiProviderRegistry registry) => _registry = registry;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var module = ctx.Node.AiModule;
        var provider = _registry.GetProvider(module.ProviderType);
        if (provider is null)
            return ModuleResult.Failed($"Proveedor '{module.ProviderType}' no disponible");

        var apiKey = module.ApiKey?.EncryptedKey;
        if (string.IsNullOrEmpty(apiKey))
            return ModuleResult.Failed("API Key no configurada");

        // Collect all inputs ordered by port name
        var inputParts = new List<string>();
        foreach (var (portId, dataList) in ctx.InputsByPort.OrderBy(kv => kv.Key))
        {
            if (!portId.StartsWith("input_")) continue;
            var part = $"## {portId}\n";
            foreach (var data in dataList)
            {
                if (!string.IsNullOrWhiteSpace(data.TextContent))
                    part += data.TextContent + "\n";
                if (data.Files is { Count: > 0 })
                {
                    part += "Archivos:\n";
                    foreach (var file in data.Files)
                        part += $"- {file.FileName} ({file.ContentType})\n";
                }
            }
            inputParts.Add(part);
        }

        var combinedInput = string.Join("\n\n", inputParts);
        if (string.IsNullOrWhiteSpace(combinedInput))
            return ModuleResult.Failed("Sin datos de entrada");

        var aiContext = new AiExecutionContext
        {
            ModuleType = "Text",
            ModelName = module.ModelName,
            ApiKey = apiKey,
            Input = combinedInput,
            ProjectContext = ctx.Project.Context,
            PreviousExecutionsSummary = ctx.PreviousSummaryContext,
            Configuration = ctx.Config,
        };

        var result = await provider.ExecuteAsync(aiContext);
        if (!result.Success)
            return ModuleResult.Failed(result.Error ?? "Error en coordinador");

        var stepOutput = OutputSchemaHelper.ParseTextOutput(result.TextOutput ?? "", result.Metadata);
        return ModuleResult.Completed(stepOutput, result.EstimatedCost);
    }
}
