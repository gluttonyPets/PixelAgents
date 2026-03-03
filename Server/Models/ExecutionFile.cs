namespace Server.Models
{
    public class ExecutionFile
    {
        public Guid Id { get; set; }
        public Guid StepExecutionId { get; set; }
        public string FileName { get; set; } = default!;
        public string ContentType { get; set; } = default!;
        public string FilePath { get; set; } = default!;
        public string Direction { get; set; } = default!;
        public long FileSize { get; set; }
        public DateTime CreatedAt { get; set; }

        public StepExecution StepExecution { get; set; } = null!;
    }
}
