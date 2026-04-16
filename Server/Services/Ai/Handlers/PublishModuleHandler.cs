namespace Server.Services.Ai.Handlers;

/// <summary>Publish module (Instagram, TikTok via Buffer/Metricool).</summary>
public class PublishModuleHandler : IModuleHandler
{
    public string ModuleType => "Publish";

    public Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        // Publishing requires external services (Metricool, Buffer)
        // The actual publishing is handled by the executor which has access to those services
        var content = ctx.GetInputText("input_content");

        var output = new StepOutput
        {
            Type = "text",
            Content = content,
            Summary = "Contenido preparado para publicacion",
        };

        // For now, return completed. The executor will handle the actual publishing.
        return Task.FromResult(ModuleResult.Completed(output));
    }
}
