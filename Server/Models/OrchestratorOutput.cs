namespace Server.Models
{
    public class OrchestratorOutput
    {
        public Guid Id { get; set; }
        public Guid ProjectModuleId { get; set; }
        public string OutputKey { get; set; } = "";
        public string Label { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string DataType { get; set; } = "text";
        public int SortOrder { get; set; }
        public Guid? TargetModuleId { get; set; } // legacy — ignored
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public ProjectModule ProjectModule { get; set; } = null!;
    }
}
