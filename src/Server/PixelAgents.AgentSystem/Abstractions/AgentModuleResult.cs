namespace PixelAgents.AgentSystem.Abstractions;

public class AgentModuleResult
{
    public bool Success { get; set; }
    public Dictionary<string, object> OutputData { get; set; } = [];
    public string? Summary { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Logs { get; set; } = [];

    public static AgentModuleResult Ok(Dictionary<string, object> data, string? summary = null)
        => new() { Success = true, OutputData = data, Summary = summary };

    public static AgentModuleResult Fail(string error)
        => new() { Success = false, ErrorMessage = error };
}
