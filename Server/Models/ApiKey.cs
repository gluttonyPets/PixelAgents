namespace Server.Models
{
    public class ApiKey
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = default!;
        public string ProviderType { get; set; } = default!;
        public string EncryptedKey { get; set; } = default!;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public ICollection<AiModule> AiModules { get; set; } = new List<AiModule>();
    }
}
