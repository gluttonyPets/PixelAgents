using Microsoft.Extensions.Logging;
using PixelAgents.Domain.ValueObjects;

namespace PixelAgents.AgentSystem.Abstractions;

public abstract class AgentModuleBase : IAgentModule
{
    protected readonly ILogger Logger;

    protected AgentModuleBase(ILogger logger)
    {
        Logger = logger;
    }

    public abstract string ModuleKey { get; }
    public abstract string DisplayName { get; }
    public abstract string Description { get; }
    public abstract string Role { get; }
    public abstract AvatarAppearance DefaultAppearance { get; }
    public abstract List<AgentSkill> Skills { get; }
    public abstract string Personality { get; }

    public abstract Task<AgentModuleResult> ExecuteAsync(AgentModuleContext context, CancellationToken ct = default);
    public abstract bool CanHandle(string taskType);

    protected void ReportProgress(AgentModuleContext context, double percentage, string message)
    {
        context.Progress?.Report(new AgentProgress(percentage, message));
        Logger.LogInformation("[{Module}] {Percentage:F0}% - {Message}", ModuleKey, percentage, message);
    }

    protected T GetInput<T>(AgentModuleContext context, string key)
    {
        if (context.InputParameters.TryGetValue(key, out var value) && value is T typed)
            return typed;

        if (context.PipelineData.TryGetValue(key, out var pipelineValue) && pipelineValue is T pipelineTyped)
            return pipelineTyped;

        throw new InvalidOperationException($"Required input '{key}' of type {typeof(T).Name} not found in context.");
    }

    protected T? GetInputOrDefault<T>(AgentModuleContext context, string key, T? defaultValue = default)
    {
        if (context.InputParameters.TryGetValue(key, out var value) && value is T typed)
            return typed;

        if (context.PipelineData.TryGetValue(key, out var pipelineValue) && pipelineValue is T pipelineTyped)
            return pipelineTyped;

        return defaultValue;
    }
}
