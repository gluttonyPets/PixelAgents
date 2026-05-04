using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;
using Xunit;

namespace Server.Tests.NoRepetirTemas;

/// <summary>
/// Tests de la logica de construccion del contexto de historial previo.
/// Replica la logica del metodo privado BuildPreviousSummaryContextAsync
/// de GraphPipelineExecutor usando InMemory DB.
///
/// Esta logica es el nucleo del feature "no-repetir temas":
/// construye un texto con los resumenes de ejecuciones anteriores para
/// enviarselo al modelo de IA y que no repita contenido ya generado.
/// </summary>
public class PreviousSummaryContextTests
{
    /// <summary>
    /// Replica la logica exacta de GraphPipelineExecutor.BuildPreviousSummaryContextAsync
    /// para poder testearla sin depender de la clase concreta.
    /// </summary>
    private static async Task<string?> BuildPreviousSummaryContextAsync(
        UserDbContext db,
        Guid projectId,
        Guid currentExecutionId,
        CancellationToken ct = default)
    {
        var previousSummaries = await db.ProjectExecutions
            .Where(e => e.ProjectId == projectId
                && e.Status == "Completed"
                && e.ExecutionSummary != null
                && e.Id != currentExecutionId)
            .OrderByDescending(e => e.CompletedAt)
            .Take(10)
            .Select(e => new { e.CompletedAt, e.ExecutionSummary })
            .ToListAsync(ct);

        if (previousSummaries.Count == 0)
            return null;

        var lines = previousSummaries
            .OrderBy(s => s.CompletedAt)
            .Select(s => $"- ({s.CompletedAt:yyyy-MM-dd HH:mm}) {s.ExecutionSummary}");
        return "[Historial de ejecuciones anteriores - NO repitas contenido ya creado]\n"
            + string.Join("\n", lines);
    }

    private static UserDbContext CreateInMemoryDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new UserDbContext(options);
    }

    private static Guid CreateProject(UserDbContext db)
    {
        var projectId = Guid.NewGuid();
        db.Projects.Add(new Project
        {
            Id = projectId,
            Name = "Test Project",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return projectId;
    }

    [Fact]
    public async Task BuildPreviousSummaryContext_SinEjecucionesPrevias_DevuelveNull()
    {
        // Arrange
        await using var db = CreateInMemoryDb(nameof(BuildPreviousSummaryContext_SinEjecucionesPrevias_DevuelveNull));
        var projectId = CreateProject(db);
        var currentId = Guid.NewGuid();

        // Act
        var result = await BuildPreviousSummaryContextAsync(db, projectId, currentId);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task BuildPreviousSummaryContext_ConEjecucionesCompletadas_DevuelveResumen()
    {
        // Arrange
        await using var db = CreateInMemoryDb(nameof(BuildPreviousSummaryContext_ConEjecucionesCompletadas_DevuelveResumen));
        var projectId = CreateProject(db);
        var currentId = Guid.NewGuid();

        db.ProjectExecutions.Add(new ProjectExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Status = "Completed",
            WorkspacePath = "/tmp/test1",
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            CompletedAt = DateTime.UtcNow.AddDays(-2),
            ExecutionSummary = "Post sobre marketing digital para Instagram",
        });
        db.ProjectExecutions.Add(new ProjectExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Status = "Completed",
            WorkspacePath = "/tmp/test2",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            CompletedAt = DateTime.UtcNow.AddDays(-1),
            ExecutionSummary = "Post sobre estrategia de contenidos para TikTok",
        });
        await db.SaveChangesAsync();

        // Act
        var result = await BuildPreviousSummaryContextAsync(db, projectId, currentId);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("[Historial de ejecuciones anteriores - NO repitas contenido ya creado]", result);
        Assert.Contains("Post sobre marketing digital para Instagram", result);
        Assert.Contains("Post sobre estrategia de contenidos para TikTok", result);
    }

    [Fact]
    public async Task BuildPreviousSummaryContext_ExcluyeEjecucionActual()
    {
        // Arrange
        await using var db = CreateInMemoryDb(nameof(BuildPreviousSummaryContext_ExcluyeEjecucionActual));
        var projectId = CreateProject(db);
        var currentId = Guid.NewGuid();

        // La ejecucion actual con summary (no debe aparecer en el contexto)
        db.ProjectExecutions.Add(new ProjectExecution
        {
            Id = currentId,
            ProjectId = projectId,
            Status = "Completed",
            WorkspacePath = "/tmp/current",
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            ExecutionSummary = "Esta es la ejecucion actual y no debe aparecer",
        });
        await db.SaveChangesAsync();

        // Act
        var result = await BuildPreviousSummaryContextAsync(db, projectId, currentId);

        // Assert: no debe incluir la ejecucion actual
        Assert.Null(result);
    }

    [Fact]
    public async Task BuildPreviousSummaryContext_ExcluyeEjecucionesFallidas()
    {
        // Arrange
        await using var db = CreateInMemoryDb(nameof(BuildPreviousSummaryContext_ExcluyeEjecucionesFallidas));
        var projectId = CreateProject(db);
        var currentId = Guid.NewGuid();

        // Ejecucion fallida NO debe aparecer en el contexto
        db.ProjectExecutions.Add(new ProjectExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Status = "Failed",
            WorkspacePath = "/tmp/failed",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            CompletedAt = DateTime.UtcNow.AddDays(-1),
            ExecutionSummary = "Post fallido que no debe aparecer",
        });
        // Ejecucion en Running tampoco
        db.ProjectExecutions.Add(new ProjectExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Status = "Running",
            WorkspacePath = "/tmp/running",
            CreatedAt = DateTime.UtcNow,
            ExecutionSummary = "Post en ejecucion que no debe aparecer",
        });
        await db.SaveChangesAsync();

        // Act
        var result = await BuildPreviousSummaryContextAsync(db, projectId, currentId);

        // Assert: solo Completed => null porque no hay ninguna completada
        Assert.Null(result);
    }

    [Fact]
    public async Task BuildPreviousSummaryContext_ExcluyeEjecucionesSinResumen()
    {
        // Arrange
        await using var db = CreateInMemoryDb(nameof(BuildPreviousSummaryContext_ExcluyeEjecucionesSinResumen));
        var projectId = CreateProject(db);
        var currentId = Guid.NewGuid();

        // Ejecucion completada pero sin resumen
        db.ProjectExecutions.Add(new ProjectExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Status = "Completed",
            WorkspacePath = "/tmp/noSummary",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            CompletedAt = DateTime.UtcNow.AddDays(-1),
            ExecutionSummary = null, // Sin resumen
        });
        await db.SaveChangesAsync();

        // Act
        var result = await BuildPreviousSummaryContextAsync(db, projectId, currentId);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task BuildPreviousSummaryContext_TomaMaximo10Ejecuciones()
    {
        // Arrange
        await using var db = CreateInMemoryDb(nameof(BuildPreviousSummaryContext_TomaMaximo10Ejecuciones));
        var projectId = CreateProject(db);
        var currentId = Guid.NewGuid();

        // Crear 15 ejecuciones completadas
        for (int i = 1; i <= 15; i++)
        {
            db.ProjectExecutions.Add(new ProjectExecution
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Status = "Completed",
                WorkspacePath = $"/tmp/exec{i}",
                CreatedAt = DateTime.UtcNow.AddDays(-i),
                CompletedAt = DateTime.UtcNow.AddDays(-i),
                ExecutionSummary = $"Post numero {i} sobre algun tema",
            });
        }
        await db.SaveChangesAsync();

        // Act
        var result = await BuildPreviousSummaryContextAsync(db, projectId, currentId);

        // Assert: el resultado no debe ser null y debe tener exactamente 10 lineas de contenido
        Assert.NotNull(result);
        // Contamos las lineas que empiezan con "- ("
        var lines = result!.Split('\n').Where(l => l.StartsWith("- (")).ToList();
        Assert.Equal(10, lines.Count);
    }

    [Fact]
    public async Task BuildPreviousSummaryContext_EjecucionesOtroProyecto_NoAparecen()
    {
        // Arrange
        await using var db = CreateInMemoryDb(nameof(BuildPreviousSummaryContext_EjecucionesOtroProyecto_NoAparecen));
        var projectId = CreateProject(db);
        var otroProjectId = CreateProject(db);
        var currentId = Guid.NewGuid();

        // Ejecucion de otro proyecto
        db.ProjectExecutions.Add(new ProjectExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = otroProjectId,
            Status = "Completed",
            WorkspacePath = "/tmp/otro",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            CompletedAt = DateTime.UtcNow.AddDays(-1),
            ExecutionSummary = "Post de otro proyecto que no debe aparecer",
        });
        await db.SaveChangesAsync();

        // Act
        var result = await BuildPreviousSummaryContextAsync(db, projectId, currentId);

        // Assert: no debe incluir ejecuciones de otro proyecto
        Assert.Null(result);
    }

    [Fact]
    public async Task BuildPreviousSummaryContext_OrdenCronologico_MasRecienteAlFinal()
    {
        // Arrange
        await using var db = CreateInMemoryDb(nameof(BuildPreviousSummaryContext_OrdenCronologico_MasRecienteAlFinal));
        var projectId = CreateProject(db);
        var currentId = Guid.NewGuid();

        var fecha1 = new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);
        var fecha2 = new DateTime(2026, 4, 15, 10, 0, 0, DateTimeKind.Utc);

        db.ProjectExecutions.Add(new ProjectExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Status = "Completed",
            WorkspacePath = "/tmp/exec1",
            CreatedAt = fecha2, // Mas reciente
            CompletedAt = fecha2,
            ExecutionSummary = "Segundo post (mas reciente)",
        });
        db.ProjectExecutions.Add(new ProjectExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Status = "Completed",
            WorkspacePath = "/tmp/exec2",
            CreatedAt = fecha1, // Mas antiguo
            CompletedAt = fecha1,
            ExecutionSummary = "Primer post (mas antiguo)",
        });
        await db.SaveChangesAsync();

        // Act
        var result = await BuildPreviousSummaryContextAsync(db, projectId, currentId);

        // Assert: el mas antiguo debe aparecer primero (orden cronologico ascendente)
        Assert.NotNull(result);
        var idxPrimero = result!.IndexOf("Primer post");
        var idxSegundo = result.IndexOf("Segundo post");
        Assert.True(idxPrimero < idxSegundo, "El post mas antiguo debe aparecer antes en el historial");
    }

    [Fact]
    public async Task BuildPreviousSummaryContext_FormatoFecha_EsCorrecto()
    {
        // Arrange
        await using var db = CreateInMemoryDb(nameof(BuildPreviousSummaryContext_FormatoFecha_EsCorrecto));
        var projectId = CreateProject(db);
        var currentId = Guid.NewGuid();

        var fechaEspecifica = new DateTime(2026, 3, 15, 9, 30, 0, DateTimeKind.Utc);

        db.ProjectExecutions.Add(new ProjectExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Status = "Completed",
            WorkspacePath = "/tmp/fecha",
            CreatedAt = fechaEspecifica,
            CompletedAt = fechaEspecifica,
            ExecutionSummary = "Post de prueba",
        });
        await db.SaveChangesAsync();

        // Act
        var result = await BuildPreviousSummaryContextAsync(db, projectId, currentId);

        // Assert: el formato de fecha debe ser "yyyy-MM-dd HH:mm"
        Assert.NotNull(result);
        Assert.Contains("(2026-03-15 09:30)", result);
    }

    [Fact]
    public async Task BuildPreviousSummaryContext_ContenidoCabecera_EsCorrecto()
    {
        // Arrange
        await using var db = CreateInMemoryDb(nameof(BuildPreviousSummaryContext_ContenidoCabecera_EsCorrecto));
        var projectId = CreateProject(db);
        var currentId = Guid.NewGuid();

        db.ProjectExecutions.Add(new ProjectExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Status = "Completed",
            WorkspacePath = "/tmp/cabecera",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            CompletedAt = DateTime.UtcNow.AddDays(-1),
            ExecutionSummary = "Contenido de prueba",
        });
        await db.SaveChangesAsync();

        // Act
        var result = await BuildPreviousSummaryContextAsync(db, projectId, currentId);

        // Assert: la cabecera exacta que se le pasa al modelo debe estar presente
        Assert.NotNull(result);
        Assert.StartsWith("[Historial de ejecuciones anteriores - NO repitas contenido ya creado]", result);
    }
}
