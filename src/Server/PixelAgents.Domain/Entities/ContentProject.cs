using PixelAgents.Domain.Enums;

namespace PixelAgents.Domain.Entities;

public class ContentProject : BaseEntity
{
    public string Title { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public string? TemplateUrl { get; set; }
    public string? FinalContentUrl { get; set; }
    public List<SocialPlatform> TargetPlatforms { get; set; } = [];
    public DateTime? ScheduledPublishDate { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = [];

    public Guid PipelineId { get; set; }
    public Pipeline Pipeline { get; set; } = null!;
}
