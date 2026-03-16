namespace Server.Models
{
    public class ModuleFile
    {
        public Guid Id { get; set; }
        public Guid AiModuleId { get; set; }
        public string FileName { get; set; } = default!;
        public string ContentType { get; set; } = default!;
        public string FilePath { get; set; } = default!;
        public long FileSize { get; set; }
        public DateTime CreatedAt { get; set; }

        public AiModule AiModule { get; set; } = null!;
    }
}
