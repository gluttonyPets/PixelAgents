namespace Server.Models
{
    public class ModuleConnection
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public Guid FromModuleId { get; set; }
        public string FromPort { get; set; } = "";
        public Guid ToModuleId { get; set; }
        public string ToPort { get; set; } = "";
        /// <summary>
        /// Optional JSON schema/shape agreed between the two modules so the
        /// upstream can format its output accordingly and the downstream can
        /// read each field in the right slot.
        /// </summary>
        public string? Format { get; set; }
        public DateTime CreatedAt { get; set; }

        public Project Project { get; set; } = null!;
        public ProjectModule FromModule { get; set; } = null!;
        public ProjectModule ToModule { get; set; } = null!;
    }
}
