namespace Server.Models
{
    public class ProjectModule
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public Guid AiModuleId { get; set; }
        public int StepOrder { get; set; }
        public string BranchId { get; set; } = "main";
        public int? BranchFromStep { get; set; }
        public string? StepName { get; set; }
        public string? InputMapping { get; set; }
        public string? Configuration { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public Project Project { get; set; } = null!;
        public AiModule AiModule { get; set; } = null!;
        public ICollection<StepExecution> StepExecutions { get; set; } = new List<StepExecution>();
    }
}
