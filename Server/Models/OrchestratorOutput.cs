namespace Server.Models
{
    public class OrchestratorOutput
    {
        public Guid Id { get; set; }
        public Guid ProjectModuleId { get; set; }
        public string OutputKey { get; set; } = "";
        public string Label { get; set; } = "";
        public string Prompt { get; set; } = "";
        public int SortOrder { get; set; }
        public Guid? TargetModuleId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public ProjectModule ProjectModule { get; set; } = null!;
        public AiModule? TargetModule { get; set; }
    }
}
