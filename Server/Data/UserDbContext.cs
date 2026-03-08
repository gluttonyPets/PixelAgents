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
            });

            // ── ProjectModule ──
            modelBuilder.Entity<ProjectModule>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.StepName).HasMaxLength(200);
                e.Property(x => x.InputMapping).HasColumnType("text");
                e.Property(x => x.Configuration).HasColumnType("text");
                e.Property(x => x.IsActive).HasDefaultValue(true);

                e.HasOne(x => x.Project)
                    .WithMany(p => p.ProjectModules)
                    .HasForeignKey(x => x.ProjectId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasOne(x => x.AiModule)
                    .WithMany(m => m.ProjectModules)
                    .HasForeignKey(x => x.AiModuleId)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasIndex(x => new { x.ProjectId, x.StepOrder }).IsUnique();
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
                e.Property(x => x.PausedStepData).HasColumnType("text");
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

                e.HasIndex(x => new { x.ExecutionId, x.StepOrder });
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
        }
    }
}
