namespace Server.Models
{
    public class ExecutionLog
    {
        public Guid Id { get; set; }
        public Guid ExecutionId { get; set; }
        public string Level { get; set; } = "info";
        public string Message { get; set; } = "";
        public int? StepOrder { get; set; }
        public string? StepName { get; set; }
        public DateTime Timestamp { get; set; }

        public ProjectExecution Execution { get; set; } = null!;
    }
}
