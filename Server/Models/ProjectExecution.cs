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
        public int? PausedAtStepOrder { get; set; }
        public string? PausedStepData { get; set; }
        public string? UserInput { get; set; }
        public decimal TotalEstimatedCost { get; set; }
        /// <summary>JSON array of branches currently paused waiting for user input.</summary>
        public string? PausedBranches { get; set; }
        /// <summary>AI-generated summary of what was produced in this execution, used as context for future runs.</summary>
        public string? ExecutionSummary { get; set; }

        public Project Project { get; set; } = null!;
        public ICollection<StepExecution> StepExecutions { get; set; } = new List<StepExecution>();
    }
}
