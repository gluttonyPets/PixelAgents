using PixelAgents.Shared.DTOs;
using PixelAgents.Shared.Enums;

namespace PixelAgents.Web.Models;

public class OfficeState
{
    public WorkspaceDto? CurrentWorkspace { get; set; }
    public List<AgentDto> Agents { get; set; } = [];
    public Dictionary<Guid, AgentVisualState> AgentVisuals { get; set; } = [];
    public List<ActivityLogEntry> ActivityLog { get; set; } = [];
    public PipelineDto? ActivePipeline { get; set; }

    public event Action? OnStateChanged;

    public void NotifyStateChanged() => OnStateChanged?.Invoke();

    public void UpdateAgentStatus(Guid agentId, AgentStatusDto status, string? activity)
    {
        var agent = Agents.FirstOrDefault(a => a.Id == agentId);
        if (agent is null) return;

        if (!AgentVisuals.TryGetValue(agentId, out var visual))
        {
            visual = new AgentVisualState(agentId);
            AgentVisuals[agentId] = visual;
        }

        visual.Status = status;
        visual.CurrentActivity = activity;
        visual.CurrentAnimation = status switch
        {
            AgentStatusDto.Working => agent.Appearance.WorkingAnimation,
            AgentStatusDto.Thinking => agent.Appearance.ThinkingAnimation,
            _ => agent.Appearance.IdleAnimation
        };

        AddLog(agent.Name, activity ?? status.ToString(), status == AgentStatusDto.Working ? "work" : "info");
        NotifyStateChanged();
    }

    public void AddLog(string agentName, string message, string type = "info")
    {
        ActivityLog.Insert(0, new ActivityLogEntry(
            DateTime.Now, agentName, message, type));

        if (ActivityLog.Count > 50)
            ActivityLog.RemoveRange(50, ActivityLog.Count - 50);
    }
}

public class AgentVisualState
{
    public Guid AgentId { get; }
    public AgentStatusDto Status { get; set; } = AgentStatusDto.Idle;
    public string CurrentAnimation { get; set; } = "idle";
    public string? CurrentActivity { get; set; }
    public int Frame { get; set; }

    public AgentVisualState(Guid agentId)
    {
        AgentId = agentId;
    }
}

public record ActivityLogEntry(
    DateTime Timestamp,
    string AgentName,
    string Message,
    string Type
);
