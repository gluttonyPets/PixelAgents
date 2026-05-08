namespace Server.Models
{
    public static class PlannedPromptStatus
    {
        public const string Pending = "Pending";
        public const string Used = "Used";
        public const string Skipped = "Skipped";
    }

    public class PlannedPrompt
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public int OrderIndex { get; set; }
        public string Content { get; set; } = default!;
        public string Status { get; set; } = PlannedPromptStatus.Pending;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? UsedAt { get; set; }
        public Guid? ExecutionId { get; set; }

        public Project Project { get; set; } = null!;
    }
}
