namespace Server.Models
{
    public class TelegramCorrelation
    {
        public Guid Id { get; set; }
        public Guid ExecutionId { get; set; }
        public string TenantDbName { get; set; } = default!;
        public string ChatId { get; set; } = default!;
        public int StepOrder { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsResolved { get; set; }
    }
}
