using System.Text.Json;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Checkpoint: pauses execution for human review.
/// Input data passes through to output when approved.
/// </summary>
public class CheckpointModuleHandler : IModuleHandler
{
    public string ModuleType => "Checkpoint";

    public Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        // Collect all input data for the user to review
        var reviewData = new Dictionary<string, object>();
        foreach (var (portId, dataList) in ctx.InputsByPort)
        {
            var items = new List<object>();
            foreach (var data in dataList)
            {
                if (!string.IsNullOrWhiteSpace(data.TextContent))
                    items.Add(data.TextContent);
                if (data.Files is { Count: > 0 })
                    items.AddRange(data.Files.Select(f => (object)new { f.FileName, f.ContentType }));
            }
            reviewData[portId] = items;
        }

        var output = new StepOutput
        {
            Type = "checkpoint",
            Content = JsonSerializer.Serialize(reviewData),
            Summary = "Esperando revision del usuario",
        };

        return Task.FromResult(ModuleResult.Paused("WaitingForCheckpoint", output));
    }
}
