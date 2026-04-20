namespace Server.Models
{
    public class WhatsAppCorrelation
    {
        public Guid Id { get; set; }
        public Guid ExecutionId { get; set; }
        public Guid ProjectModuleId { get; set; }
        public string TenantDbName { get; set; } = default!;
        public string RecipientNumber { get; set; } = default!;
        public DateTime CreatedAt { get; set; }
        public bool IsResolved { get; set; }
    }
}
