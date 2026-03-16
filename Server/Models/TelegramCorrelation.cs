namespace Server.Models
{
    public class TelegramCorrelation
    {
        public Guid Id { get; set; }
        public Guid ExecutionId { get; set; }
        public string TenantDbName { get; set; } = default!;
        public string ChatId { get; set; } = default!;
        public int StepOrder { get; set; }
        /// <summary>Branch that owns this interaction step. Null means main pipeline.</summary>
        public string? BranchId { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsResolved { get; set; }
        /// <summary>
        /// Tracks the correlation state: "waiting" (default), "awaiting_restart" (waiting for restart clarification text).
        /// </summary>
        public string State { get; set; } = "waiting";
    }
}
