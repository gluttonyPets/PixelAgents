namespace Server.Models
{
    public class AiModule
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = default!;
        public string? Description { get; set; }
        public string ProviderType { get; set; } = default!;
        public string ModuleType { get; set; } = default!;
        public string ModelName { get; set; } = default!;
        public Guid? ApiKeyId { get; set; }
        public string? Configuration { get; set; }
        public bool IsEnabled { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public ApiKey? ApiKey { get; set; }
        public ICollection<ProjectModule> ProjectModules { get; set; } = new List<ProjectModule>();
        public ICollection<ModuleFile> Files { get; set; } = new List<ModuleFile>();
    }
}
