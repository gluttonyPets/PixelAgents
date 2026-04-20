using Microsoft.EntityFrameworkCore;
using Server.Models;

namespace Server.Data
{
    public class UserDbContext : DbContext
    {
        public UserDbContext(DbContextOptions<UserDbContext> options) : base(options) { }

        public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
        public DbSet<AiModule> AiModules => Set<AiModule>();
        public DbSet<Project> Projects => Set<Project>();
        public DbSet<ProjectModule> ProjectModules => Set<ProjectModule>();
        public DbSet<ProjectExecution> ProjectExecutions => Set<ProjectExecution>();
        public DbSet<StepExecution> StepExecutions => Set<StepExecution>();
        public DbSet<ExecutionFile> ExecutionFiles => Set<ExecutionFile>();
        public DbSet<ExecutionLog> ExecutionLogs => Set<ExecutionLog>();
        public DbSet<ProjectSchedule> ProjectSchedules => Set<ProjectSchedule>();
        public DbSet<ModuleFile> ModuleFiles => Set<ModuleFile>();
        public DbSet<ModuleConnection> ModuleConnections => Set<ModuleConnection>();
        public DbSet<OrchestratorOutput> OrchestratorOutputs => Set<OrchestratorOutput>();
        public DbSet<Rule> Rules => Set<Rule>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // ── ApiKey ──
            modelBuilder.Entity<ApiKey>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Name).IsRequired().HasMaxLength(200);
                e.Property(x => x.ProviderType).IsRequired().HasMaxLength(100);
                e.Property(x => x.EncryptedKey).IsRequired();
            });

            // ── AiModule ──
            modelBuilder.Entity<AiModule>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Name).IsRequired().HasMaxLength(200);
                e.Property(x => x.Description).HasMaxLength(2000);
                e.Property(x => x.ProviderType).IsRequired().HasMaxLength(100);
                e.Property(x => x.ModuleType).IsRequired().HasMaxLength(100);
                e.Property(x => x.ModelName).IsRequired().HasMaxLength(200);
                e.Property(x => x.Configuration).HasColumnType("text");
                e.Property(x => x.IsEnabled).HasDefaultValue(true);

                e.HasOne(x => x.ApiKey)
                    .WithMany(k => k.AiModules)
                    .HasForeignKey(x => x.ApiKeyId)
                    .OnDelete(DeleteBehavior.SetNull);

                e.HasIndex(x => x.ProviderType);
                e.HasIndex(x => x.ModuleType);
            });

            // ── Project ──
            modelBuilder.Entity<Project>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Name).IsRequired().HasMaxLength(200);
                e.Property(x => x.Description).HasMaxLength(2000);
                e.Property(x => x.Context).HasColumnType("text");
                e.Property(x => x.WhatsAppConfig).HasColumnType("text");
                e.Property(x => x.TelegramConfig).HasColumnType("text");
                e.Property(x => x.InstagramConfig).HasColumnType("text");
                e.Property(x => x.TikTokConfig).HasColumnType("text");
                e.Property(x => x.GraphLayout).HasColumnType("text");

            });

            // ── ProjectModule ──
            modelBuilder.Entity<ProjectModule>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.StepName).HasMaxLength(200);
                e.Property(x => x.Configuration).HasColumnType("text");
                e.Property(x => x.IsActive).HasDefaultValue(true);
                e.Property(x => x.PosX).HasDefaultValue(0.0);
                e.Property(x => x.PosY).HasDefaultValue(0.0);

                e.HasOne(x => x.Project)
                    .WithMany(p => p.ProjectModules)
                    .HasForeignKey(x => x.ProjectId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasOne(x => x.AiModule)
                    .WithMany(m => m.ProjectModules)
                    .HasForeignKey(x => x.AiModuleId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasIndex(x => x.ProjectId);
            });

            // ── ModuleConnection ──
            modelBuilder.Entity<ModuleConnection>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.FromPort).IsRequired().HasMaxLength(100);
                e.Property(x => x.ToPort).IsRequired().HasMaxLength(100);
                e.Property(x => x.Format).HasColumnType("text");

                e.HasOne(x => x.Project)
                    .WithMany()
                    .HasForeignKey(x => x.ProjectId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasOne(x => x.FromModule)
                    .WithMany(m => m.OutgoingConnections)
                    .HasForeignKey(x => x.FromModuleId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasOne(x => x.ToModule)
                    .WithMany(m => m.IncomingConnections)
                    .HasForeignKey(x => x.ToModuleId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasIndex(x => x.ProjectId);
                e.HasIndex(x => new { x.FromModuleId, x.FromPort });
                e.HasIndex(x => new { x.ToModuleId, x.ToPort });
            });

            // ── ProjectExecution ──
            modelBuilder.Entity<ProjectExecution>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Status).IsRequired().HasMaxLength(50);
                e.Property(x => x.WorkspacePath).IsRequired().HasMaxLength(500);

                e.HasOne(x => x.Project)
                    .WithMany(p => p.Executions)
                    .HasForeignKey(x => x.ProjectId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasIndex(x => x.ProjectId);
                e.Property(x => x.PausedAtModuleId);
                e.Property(x => x.PausedStepData).HasColumnType("text");
                e.Property(x => x.UserInput).HasColumnType("text");
            });

            // ── StepExecution ──
            modelBuilder.Entity<StepExecution>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Status).IsRequired().HasMaxLength(50);
                e.Property(x => x.InputData).HasColumnType("text");
                e.Property(x => x.OutputData).HasColumnType("text");
                e.Property(x => x.ErrorMessage).HasColumnType("text");

                e.HasOne(x => x.Execution)
                    .WithMany(ex => ex.StepExecutions)
                    .HasForeignKey(x => x.ExecutionId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasOne(x => x.ProjectModule)
                    .WithMany(pm => pm.StepExecutions)
                    .HasForeignKey(x => x.ProjectModuleId)
                    .OnDelete(DeleteBehavior.Restrict);

                e.HasIndex(x => x.ExecutionId);
                e.HasIndex(x => new { x.ExecutionId, x.ProjectModuleId });
            });

            // ── ProjectSchedule ──
            modelBuilder.Entity<ProjectSchedule>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.CronExpression).IsRequired().HasMaxLength(100);
                e.Property(x => x.TimeZone).IsRequired().HasMaxLength(100).HasDefaultValue("UTC");
                e.Property(x => x.UserInput).HasColumnType("text");
                e.Property(x => x.IsEnabled).HasDefaultValue(true);

                e.HasOne(x => x.Project)
                    .WithMany()
                    .HasForeignKey(x => x.ProjectId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasIndex(x => x.ProjectId);
                e.HasIndex(x => new { x.IsEnabled, x.NextRunAt });
            });

            // ── ExecutionLog ──
            modelBuilder.Entity<ExecutionLog>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Level).IsRequired().HasMaxLength(20);
                e.Property(x => x.Message).IsRequired().HasColumnType("text");
                e.Property(x => x.ModuleName).HasMaxLength(200);

                e.HasOne(x => x.Execution)
                    .WithMany()
                    .HasForeignKey(x => x.ExecutionId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasIndex(x => x.ExecutionId);
            });

            // ── ExecutionFile ──
            modelBuilder.Entity<ExecutionFile>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.FileName).IsRequired().HasMaxLength(500);
                e.Property(x => x.ContentType).IsRequired().HasMaxLength(100);
                e.Property(x => x.FilePath).IsRequired().HasMaxLength(1000);
                e.Property(x => x.Direction).IsRequired().HasMaxLength(20);

                e.HasOne(x => x.StepExecution)
                    .WithMany(s => s.Files)
                    .HasForeignKey(x => x.StepExecutionId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasIndex(x => x.StepExecutionId);
            });

            // ── OrchestratorOutput ──
            modelBuilder.Entity<OrchestratorOutput>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.OutputKey).IsRequired().HasMaxLength(100);
                e.Property(x => x.Label).IsRequired().HasMaxLength(500);
                e.Property(x => x.Prompt).IsRequired().HasColumnType("text");
                e.Property(x => x.DataType).IsRequired().HasMaxLength(50).HasDefaultValue("text");

                e.HasOne(x => x.ProjectModule)
                    .WithMany(pm => pm.OrchestratorOutputs)
                    .HasForeignKey(x => x.ProjectModuleId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasIndex(x => x.ProjectModuleId);

                e.Ignore(x => x.TargetModuleId);
            });

            // ── Rule ──
            modelBuilder.Entity<Rule>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Title).IsRequired().HasMaxLength(200);
                e.Property(x => x.Content).IsRequired().HasColumnType("text");
                e.Property(x => x.IsActive).HasDefaultValue(true);
                e.HasIndex(x => new { x.IsActive, x.SortOrder });
            });

            // ── ModuleFile ──
            modelBuilder.Entity<ModuleFile>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.FileName).IsRequired().HasMaxLength(500);
                e.Property(x => x.ContentType).IsRequired().HasMaxLength(100);
                e.Property(x => x.FilePath).IsRequired().HasMaxLength(1000);

                e.HasOne(x => x.AiModule)
                    .WithMany(m => m.Files)
                    .HasForeignKey(x => x.AiModuleId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasIndex(x => x.AiModuleId);
            });
        }
    }
}
