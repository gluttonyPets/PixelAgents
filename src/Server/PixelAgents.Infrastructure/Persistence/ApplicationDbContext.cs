using Microsoft.EntityFrameworkCore;
using PixelAgents.Application.Common.Interfaces;
using PixelAgents.Domain.Entities;

namespace PixelAgents.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext, IApplicationDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options) { }

    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<PipelineStep> PipelineSteps => Set<PipelineStep>();
    public DbSet<AgentTask> AgentTasks => Set<AgentTask>();
    public DbSet<ContentProject> ContentProjects => Set<ContentProject>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Agent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ModuleKey).HasMaxLength(50).IsRequired();

            entity.OwnsOne(e => e.Appearance, a =>
            {
                a.Property(p => p.SpriteSheet).HasMaxLength(200);
                a.Property(p => p.DeskStyle).HasMaxLength(50);
            });

            entity.Property(e => e.Skills)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<List<Domain.ValueObjects.AgentSkill>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());

            entity.HasOne(e => e.Workspace)
                .WithMany(w => w.Agents)
                .HasForeignKey(e => e.WorkspaceId);
        });

        modelBuilder.Entity<Workspace>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<Pipeline>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();

            entity.HasOne(e => e.Workspace)
                .WithMany(w => w.Pipelines)
                .HasForeignKey(e => e.WorkspaceId);
        });

        modelBuilder.Entity<PipelineStep>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ModuleKey).HasMaxLength(50).IsRequired();

            entity.Property(e => e.InputParameters)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());

            entity.Property(e => e.OutputData)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());

            entity.HasOne(e => e.Pipeline)
                .WithMany(p => p.Steps)
                .HasForeignKey(e => e.PipelineId);

            entity.HasOne(e => e.AssignedAgent)
                .WithMany()
                .HasForeignKey(e => e.AssignedAgentId);
        });

        modelBuilder.Entity<AgentTask>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(200).IsRequired();

            entity.HasOne(e => e.Agent)
                .WithMany(a => a.AssignedTasks)
                .HasForeignKey(e => e.AgentId);
        });

        modelBuilder.Entity<ContentProject>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Topic).HasMaxLength(500).IsRequired();

            entity.Property(e => e.TargetPlatforms)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<List<Domain.Enums.SocialPlatform>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());

            entity.Property(e => e.Metadata)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());
        });

        base.OnModelCreating(modelBuilder);
    }
}
