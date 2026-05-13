namespace Server.Models
{
    public class ModuleFile
    {
        public Guid Id { get; set; }
        public Guid ProjectModuleId { get; set; }
        public string FileName { get; set; } = default!;
        public string ContentType { get; set; } = default!;
        public string FilePath { get; set; } = default!;
        public long FileSize { get; set; }
        public DateTime CreatedAt { get; set; }

        public ProjectModule ProjectModule { get; set; } = null!;
    }
}
