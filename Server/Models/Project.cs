namespace Server.Models
{
    public class Project
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = default!;
        public string? Description { get; set; }
        public string? Context { get; set; }
        public string? WhatsAppConfig { get; set; }
        public string? TelegramConfig { get; set; }
        public string? InstagramConfig { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public ICollection<ProjectModule> ProjectModules { get; set; } = new List<ProjectModule>();
        public ICollection<ProjectExecution> Executions { get; set; } = new List<ProjectExecution>();
    }
}
