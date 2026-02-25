namespace PixelAgents.Domain.Entities;

public class Workspace : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Theme { get; set; } = "default-office";
    public List<Agent> Agents { get; set; } = [];
    public List<Pipeline> Pipelines { get; set; } = [];
}
