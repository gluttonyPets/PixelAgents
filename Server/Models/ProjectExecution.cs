namespace Server.Models
{
    public class ProjectExecution
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public string Status { get; set; } = "Pending";
        public string WorkspacePath { get; set; } = default!;
        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        public Project Project { get; set; } = null!;
        public ICollection<StepExecution> StepExecutions { get; set; } = new List<StepExecution>();
    }
}
