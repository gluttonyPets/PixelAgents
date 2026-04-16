namespace Server.Services.Ai.Handlers;

/// <summary>Emits a fixed text value configured on the module. No AI call.</summary>
public class StaticTextModuleHandler : IModuleHandler
{
    public string ModuleType => "StaticText";

    public Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var content = ctx.GetConfig("content", "");

        var output = new StepOutput
        {
            Type = "text",
            Content = content,
        };

        return Task.FromResult(ModuleResult.Completed(output));
    }
}
