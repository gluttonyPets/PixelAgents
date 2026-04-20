using System.Text.Json;
using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Orchestrator: generates multiple structured outputs from a single prompt.
/// Each output becomes available on a separate output port (output_1, output_2, etc.)
/// </summary>
public class OrchestratorModuleHandler : IModuleHandler
{
    private readonly IAiProviderRegistry _registry;
    public string ModuleType => "Orchestrator";

    public OrchestratorModuleHandler(IAiProviderRegistry registry) => _registry = registry;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var prompt = ctx.GetInputText("input_prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return ModuleResult.Failed("Sin instrucciones de entrada");

        var module = ctx.Node.AiModule;
        var pm = ctx.Node.ProjectModule;

        // Use the orchestrator's own model or fallback
        var effectiveModel = ctx.GetConfig("modelName", module.ModelName);
        var effectiveProvider = ctx.GetConfig("providerName", module.ProviderType);

        var provider = _registry.GetProvider(effectiveProvider);
        if (provider is null)
            return ModuleResult.Failed($"Proveedor '{effectiveProvider}' no disponible");

        var apiKey = module.ApiKey?.EncryptedKey
            ?? ctx.GetConfig("apiKey", "");
        if (string.IsNullOrEmpty(apiKey))
            return ModuleResult.Failed("API Key no configurada");

        // Load orchestrator outputs configuration
        var outputs = pm.OrchestratorOutputs?.OrderBy(o => o.SortOrder).ToList() ?? [];
        if (outputs.Count == 0)
            return ModuleResult.Failed("Orquestador sin salidas configuradas");

        // Build system prompt with output schema
        var extraPrompt = ctx.GetConfig("systemPrompt", "");
        var projectContext = string.IsNullOrWhiteSpace(extraPrompt)
            ? ctx.Project.Context
            : $"{ctx.Project.Context}\n\n{extraPrompt}";
        var systemPrompt = OrchestratorSchemaHelper.BuildFixedOutputPrompt(outputs, projectContext);

        var config = new Dictionary<string, object>(ctx.Config)
        {
            ["systemPrompt"] = systemPrompt,
        };

        var aiContext = new AiExecutionContext
        {
            ModuleType = "Text",
            ModelName = effectiveModel,
            ApiKey = apiKey,
            Input = prompt,
            ProjectContext = ctx.Project.Context,
            PreviousExecutionsSummary = ctx.PreviousSummaryContext,
            MandatoryRules = ctx.MandatoryRules,
            Configuration = config,
            SkipOutputSchema = true,
        };

        StepPayloadBuilder.RecordSentPayload(ctx, aiContext, effectiveProvider);

        var result = await provider.ExecuteAsync(aiContext);
        if (!result.Success)
            return ModuleResult.Failed(result.Error ?? "Error en orquestador");

        // Parse the structured plan
        var plan = OrchestratorSchemaHelper.ParseFixedPlan(result.TextOutput ?? "");
        if (plan is null)
            return ModuleResult.Failed("No se pudo parsear la salida del orquestador");

        var validationErrors = OrchestratorSchemaHelper.ValidateFixedPlan(plan, outputs);
        if (validationErrors.Count > 0)
            return ModuleResult.Failed("Salida del orquestador invalida: " + string.Join("; ", validationErrors));

        // Build output items
        var items = new List<OutputItem>();
        var contentByKey = plan.Outputs.ToDictionary(o => o.OutputKey, o => o.Content);
        foreach (var output in outputs)
        {
            var content = contentByKey.GetValueOrDefault(output.OutputKey, "");
            items.Add(new OutputItem { Content = content, Label = output.Label });
        }

        var stepOutput = new StepOutput
        {
            Type = "orchestrator",
            Title = plan.Summary,
            Content = $"Orquestador: {outputs.Count} salidas generadas",
            Summary = plan.Summary,
            Items = items,
            Metadata = new Dictionary<string, object>
            {
                ["outputs"] = outputs.Select(o => new Dictionary<string, object>
                {
                    ["outputKey"] = o.OutputKey,
                    ["label"] = o.Label,
                    ["dataType"] = o.DataType,
                }).ToList()
            }
        };

        var producedFiles = new List<ProducedFile>();
        if (!string.IsNullOrEmpty(result.TextOutput))
        {
            producedFiles.Add(new ProducedFile
            {
                Data = System.Text.Encoding.UTF8.GetBytes(result.TextOutput),
                FileName = "orchestrator_plan.json",
                ContentType = "application/json",
            });
        }

        return ModuleResult.Completed(stepOutput, result.EstimatedCost, producedFiles);
    }
}
