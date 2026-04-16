namespace Server.Services.Ai.Handlers;

/// <summary>
/// Interaction: sends a message via Telegram/WhatsApp and optionally waits for response.
/// Always pauses when waitForResponse is true.
/// </summary>
public class InteractionModuleHandler : IModuleHandler
{
    public string ModuleType => "Interaction";

    public Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var message = ctx.GetInputText("input_message");

        // The actual message sending is handled by the executor (needs Telegram/WhatsApp services)
        // This handler just prepares the output and signals pause if needed
        var waitForResponse = ctx.GetConfigBool("waitForResponse", true);

        var output = new StepOutput
        {
            Type = "text",
            Content = message,
            Summary = "Esperando respuesta del usuario",
        };

        if (waitForResponse)
            return Task.FromResult(ModuleResult.Paused("WaitingForInput", output));

        // Fire-and-forget: just pass the message through
        return Task.FromResult(ModuleResult.Completed(output));
    }
}
