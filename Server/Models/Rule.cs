namespace Server.Models
{
    /// <summary>
    /// A mandatory rule that gets injected into the system prompt of every AI
    /// call (Text, Image, Coordinator, Orchestrator, Design). Tenant-scoped:
    /// each tenant DB has its own rules and at least one is seeded on creation.
    /// </summary>
    public class Rule
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public string Content { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public int SortOrder { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
