namespace Server.Services.Ai.Handlers;

/// <summary>
/// The Start module has no inputs and emits the user's prompt as output.
/// Every pipeline must have exactly one Start module as the entry point.
/// </summary>
public class StartModuleHandler : IModuleHandler
{
    public string ModuleType => "Start";

    public Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var userInput = ctx.Graph.UserInput ?? "";

        var output = new StepOutput
        {
            Type = "text",
            Content = userInput,
            Items = [new OutputItem { Content = userInput, Label = "Prompt" }],
        };

        return Task.FromResult(ModuleResult.Completed(output));
    }
}
