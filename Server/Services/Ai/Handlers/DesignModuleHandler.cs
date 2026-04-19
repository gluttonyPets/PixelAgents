using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>Design creation module (Canva).</summary>
public class DesignModuleHandler : IModuleHandler
{
    private readonly IAiProviderRegistry _registry;
    public string ModuleType => "Design";

    public DesignModuleHandler(IAiProviderRegistry registry) => _registry = registry;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var prompt = ctx.GetInputText("input_prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return ModuleResult.Failed("Sin instrucciones de entrada");

        var module = ctx.Node.AiModule;
        var provider = _registry.GetProvider(module.ProviderType);
        if (provider is null)
            return ModuleResult.Failed($"Proveedor '{module.ProviderType}' no disponible");

        var apiKey = module.ApiKey?.EncryptedKey ?? ctx.GetConfig("accessToken", "");
        if (string.IsNullOrEmpty(apiKey))
            return ModuleResult.Failed("Token de acceso no configurado");

        var aiContext = new AiExecutionContext
        {
            ModuleType = module.ModuleType,
            ModelName = module.ModelName,
            ApiKey = apiKey,
            Input = prompt,
            ProjectContext = ctx.Project.Context,
            InitialUserInput = ctx.Execution.UserInput,
            Configuration = ctx.Config,
        };

        StepPayloadBuilder.RecordSentPayload(ctx, aiContext, module.ProviderType);

        var result = await provider.ExecuteAsync(aiContext);
        if (!result.Success)
            return ModuleResult.Failed(result.Error ?? "Error en diseno");

        var producedFiles = new List<ProducedFile>();
        var outputFiles = new List<OutputFile>();

        if (result.FileOutput is { Length: > 0 })
        {
            var ext = result.ContentType switch
            {
                "application/pdf" => ".pdf",
                "image/png" => ".png",
                _ => ".jpg"
            };
            var fileName = $"design{ext}";
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
            });
        }

        var output = new StepOutput
        {
            Type = "file",
            Content = result.TextOutput ?? "Diseno generado",
            Files = outputFiles,
        };

        return ModuleResult.Completed(output, result.EstimatedCost, producedFiles);
    }
}
