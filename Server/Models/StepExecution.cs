namespace Server.Models
{
    public class StepExecution
    {
        public Guid Id { get; set; }
        public Guid ExecutionId { get; set; }
        public Guid ProjectModuleId { get; set; }
        public int StepOrder { get; set; }
        public string Status { get; set; } = "Pending";
        public string? InputData { get; set; }
        public string? OutputData { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public decimal EstimatedCost { get; set; }

        public ProjectExecution Execution { get; set; } = null!;
        public ProjectModule ProjectModule { get; set; } = null!;
        public ICollection<ExecutionFile> Files { get; set; } = new List<ExecutionFile>();
    }
}
