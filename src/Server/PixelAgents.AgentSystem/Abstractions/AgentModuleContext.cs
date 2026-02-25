namespace PixelAgents.AgentSystem.Abstractions;

public class AgentModuleContext
{
    public Guid TaskId { get; set; }
    public Guid PipelineId { get; set; }
    public Guid AgentId { get; set; }
    public Dictionary<string, object> InputParameters { get; set; } = [];
    public Dictionary<string, object> PipelineData { get; set; } = [];
    public IProgress<AgentProgress>? Progress { get; set; }
}

public record AgentProgress(double Percentage, string Message);
