using Server.Models;

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
        var previousText = ctx.GetInputText("input_message");
        var inputFiles = ctx.GetInputFiles("input_message");

        var template = ctx.GetConfig("messageTemplate", "{previous_output}");
        if (string.IsNullOrWhiteSpace(template))
            template = "{previous_output}";

        var message = template
            .Replace("{previous_output}", previousText ?? "")
            .Replace("{step_number}", ctx.Node.ProjectModule.StepName ?? ctx.Node.AiModule.Name)
            .Replace("{module_name}", ctx.Node.ProjectModule.StepName ?? ctx.Node.AiModule.Name)
            .Trim();

        if (string.IsNullOrWhiteSpace(message))
        {
            var stepName = ctx.Node.ProjectModule.StepName ?? ctx.Node.AiModule.Name ?? "";
            message = string.IsNullOrWhiteSpace(stepName)
                ? "Revisa el contenido y confirma."
                : $"[{stepName}] Revisa el contenido y confirma.";
        }

        var waitForResponse = ctx.GetConfigBool("waitForResponse", true);

        var output = new StepOutput
        {
            Type = inputFiles.Count > 0 ? "interaction" : "text",
            Content = message,
            Summary = waitForResponse ? "Esperando respuesta del usuario" : "Mensaje enviado",
            Files = inputFiles,
        };

        var messageType = ctx.GetConfig("messageType", "combined");
        if (!string.IsNullOrWhiteSpace(messageType))
            output.Metadata["messageType"] = messageType;

        if (waitForResponse)
            return Task.FromResult(ModuleResult.Paused("WaitingForInput", output));

        return Task.FromResult(ModuleResult.Completed(output));
    }
}
