namespace Server.Models
{
    public class Project
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = default!;
        public string? Description { get; set; }
        public string? Context { get; set; }
        public string? GraphLayout { get; set; }

        // ── Conexiones asignadas (credenciales reutilizables definidas globalmente) ──
        public Guid? InstagramConnectionId { get; set; }
        public Guid? TikTokConnectionId { get; set; }
        public Guid? PinterestConnectionId { get; set; }
        public Guid? TelegramConnectionId { get; set; }
        public Guid? ShopifyConnectionId { get; set; }

        public SocialConnection? InstagramConnection { get; set; }
        public SocialConnection? TikTokConnection { get; set; }
        public SocialConnection? PinterestConnection { get; set; }
        public MessagingConnection? TelegramConnection { get; set; }
        public ShopifyConnection? ShopifyConnection { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public ICollection<ProjectModule> ProjectModules { get; set; } = new List<ProjectModule>();
        public ICollection<ProjectExecution> Executions { get; set; } = new List<ProjectExecution>();
    }
}
