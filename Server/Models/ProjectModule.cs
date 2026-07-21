namespace Server.Models
{
    public class ProjectModule
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public Guid AiModuleId { get; set; }

        /// <summary>
        /// Cuando este nodo es de tipo "SubProject", apunta al Project que se
        /// ejecuta completo como un unico modulo dentro del pipeline padre.
        /// Es una referencia viva (se ejecuta la definicion actual del proyecto
        /// insertado). Null para cualquier otro tipo de modulo.
        /// </summary>
        public Guid? SubProjectId { get; set; }

        public string? StepName { get; set; }
        public string? Configuration { get; set; }
        public bool IsActive { get; set; } = true;
        public double PosX { get; set; }
        public double PosY { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public Project Project { get; set; } = null!;
        public AiModule AiModule { get; set; } = null!;
        public ICollection<StepExecution> StepExecutions { get; set; } = new List<StepExecution>();
        public ICollection<ModuleConnection> OutgoingConnections { get; set; } = new List<ModuleConnection>();
        public ICollection<ModuleConnection> IncomingConnections { get; set; } = new List<ModuleConnection>();
        public ICollection<OrchestratorOutput> OrchestratorOutputs { get; set; } = new List<OrchestratorOutput>();
        public ICollection<ModuleFile> Files { get; set; } = new List<ModuleFile>();
    }
}
