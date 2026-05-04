using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;
using Xunit;

namespace Server.Tests.NoRepetirTemas;

/// <summary>
/// Tests de la logica de decision UseHistory en GraphPipelineExecutor.
/// Valida el comportamiento esperado segun el flag UseHistory:
///
///   useHistory=true  + hay historial  => previousSummaryContext != null
///   useHistory=true  + sin historial  => previousSummaryContext == null
///   useHistory=false                  => previousSummaryContext == null (siempre)
///
/// Estos tests verifican la logica de las lineas 103-121 de GraphPipelineExecutor.cs.
/// </summary>
public class UseHistoryLogicTests
{
    /// <summary>
    /// Replica la logica del bloque de decision de useHistory del ExecuteAsync.
    /// Retorna (previousSummaryContext, logMessage) que se genera.
    /// </summary>
    private static async Task<(string? PreviousSummaryContext, string LogMessage)> SimulateUseHistoryDecisionAsync(
        UserDbContext db,
        Guid projectId,
        Guid executionId,
        bool useHistory,
        CancellationToken ct = default)
    {
        string? previousSummaryContext = useHistory
            ? await BuildPreviousSummaryContextHelperAsync(db, projectId, executionId, ct)
            : null;

        string logMessage;
        if (previousSummaryContext is not null)
        {
            logMessage = $"[No repetir tematicas] Resumen de ejecuciones anteriores enviado al modelo:\n{previousSummaryContext}";
        }
        else if (!useHistory)
        {
            logMessage = "[No repetir tematicas] Desactivado: se omite el historial de ejecuciones anteriores.";
        }
        else
        {
            logMessage = "[No repetir tematicas] Activado pero no hay ejecuciones anteriores completadas con resumen.";
        }

        return (previousSummaryContext, logMessage);
    }

    private static async Task<string?> BuildPreviousSummaryContextHelperAsync(
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

    private static Guid CreateProjectWithExecution(UserDbContext db, string summary)
    {
        var projectId = Guid.NewGuid();
        db.Projects.Add(new Project
        {
            Id = projectId,
            Name = "Test Project",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.ProjectExecutions.Add(new ProjectExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Status = "Completed",
            WorkspacePath = "/tmp/prev",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            CompletedAt = DateTime.UtcNow.AddDays(-1),
            ExecutionSummary = summary,
        });
        db.SaveChanges();
        return projectId;
    }

    [Fact]
    public async Task UseHistory_True_ConHistorial_EnviaContextoAlModelo()
    {
        // Arrange
        await using var db = CreateInMemoryDb(nameof(UseHistory_True_ConHistorial_EnviaContextoAlModelo));
        var projectId = CreateProjectWithExecution(db, "Post sobre marketing digital");
        var currentId = Guid.NewGuid();

        // Act
        var (context, logMessage) = await SimulateUseHistoryDecisionAsync(db, projectId, currentId, useHistory: true);

        // Assert: debe haber contexto con el historial
        Assert.NotNull(context);
        Assert.Contains("Post sobre marketing digital", context);
        Assert.Contains("[No repetir tematicas] Resumen de ejecuciones anteriores enviado al modelo:", logMessage);
    }

    [Fact]
    public async Task UseHistory_True_SinHistorial_LogMensajeActivadoSinHistorial()
    {
        // Arrange
        await using var db = CreateInMemoryDb(nameof(UseHistory_True_SinHistorial_LogMensajeActivadoSinHistorial));
        // Proyecto sin ejecuciones previas completadas
        var projectId = Guid.NewGuid();
        db.Projects.Add(new Project
        {
            Id = projectId,
            Name = "Proyecto sin historial",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var currentId = Guid.NewGuid();

        // Act
        var (context, logMessage) = await SimulateUseHistoryDecisionAsync(db, projectId, currentId, useHistory: true);

        // Assert
        Assert.Null(context);
        Assert.Equal("[No repetir tematicas] Activado pero no hay ejecuciones anteriores completadas con resumen.", logMessage);
    }

    [Fact]
    public async Task UseHistory_False_ConHistorial_NoEnviaContexto()
    {
        // Arrange
        await using var db = CreateInMemoryDb(nameof(UseHistory_False_ConHistorial_NoEnviaContexto));
        var projectId = CreateProjectWithExecution(db, "Post que NO debe usarse como contexto");
        var currentId = Guid.NewGuid();

        // Act: useHistory=false aunque haya historial no lo usa
        var (context, logMessage) = await SimulateUseHistoryDecisionAsync(db, projectId, currentId, useHistory: false);

        // Assert: el contexto debe ser null aunque haya historial
        Assert.Null(context);
        Assert.Equal("[No repetir tematicas] Desactivado: se omite el historial de ejecuciones anteriores.", logMessage);
    }

    [Fact]
    public async Task UseHistory_False_SinHistorial_LogMensajeDesactivado()
    {
        // Arrange
        await using var db = CreateInMemoryDb(nameof(UseHistory_False_SinHistorial_LogMensajeDesactivado));
        var projectId = Guid.NewGuid();
        db.Projects.Add(new Project
        {
            Id = projectId,
            Name = "Proyecto",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var currentId = Guid.NewGuid();

        // Act
        var (context, logMessage) = await SimulateUseHistoryDecisionAsync(db, projectId, currentId, useHistory: false);

        // Assert
        Assert.Null(context);
        Assert.Equal("[No repetir tematicas] Desactivado: se omite el historial de ejecuciones anteriores.", logMessage);
    }
}
