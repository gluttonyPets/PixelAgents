using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Moq;
using Server.Data;
using Server.Models;
using Server.Services;
using Server.Services.Ai;
using Server.Services.Telegram;
using Xunit;

namespace Server.Tests.SiguienteEjecucion;

/// <summary>
/// Tests del botón "Siguiente ejecución" del mensaje de interacción ("Revisa el contenido").
/// Al pulsarlo, la ejecución en curso se cancela como "cancelado por usuario" y se lanza de
/// inmediato la siguiente temática (siguiente <see cref="PlannedPrompt"/> pendiente). Si la cola
/// está vacía, se abre una petición de planificación (awaiting_planning).
/// </summary>
public class NextExecutionHandlerTests
{
    private const string ChatId = "12345";

    private static CoreDbContext CreateCoreDb(string name) =>
        new(new DbContextOptionsBuilder<CoreDbContext>().UseInMemoryDatabase(name).Options);

    private static UserDbContext CreateUserDb(string name) =>
        new(new DbContextOptionsBuilder<UserDbContext>().UseInMemoryDatabase(name).Options);

    /// <summary>Handler HTTP que responde 200 a todo — las llamadas a Telegram son no-ops en tests.</summary>
    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}"),
            });
    }

    private static JsonElement CallbackUpdate(string data, string chatId)
    {
        var json = $$"""
        { "callback_query": { "id": "cbq1", "data": "{{data}}", "message": { "chat": { "id": {{chatId}} } } } }
        """;
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public async Task NextExecution_CancelaLaActualYLanzaLaSiguienteTematica()
    {
        var userDbName = nameof(NextExecution_CancelaLaActualYLanzaLaSiguienteTematica) + "_user";
        var coreDbName = nameof(NextExecution_CancelaLaActualYLanzaLaSiguienteTematica) + "_core";

        var projectId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var promptId = Guid.NewGuid();

        await using (var seed = CreateUserDb(userDbName))
        {
            seed.Projects.Add(new Project { Id = projectId, Name = "Proj", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            seed.ProjectExecutions.Add(new ProjectExecution
            {
                Id = executionId,
                ProjectId = projectId,
                Status = "WaitingForInput",
                WorkspacePath = "/tmp/ws",
                CreatedAt = DateTime.UtcNow,
            });
            seed.PlannedPrompts.Add(new PlannedPrompt
            {
                Id = promptId,
                ProjectId = projectId,
                OrderIndex = 0,
                Content = "Tema siguiente",
                Status = PlannedPromptStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var coreDb = CreateCoreDb(coreDbName);
        coreDb.TelegramCorrelations.Add(new TelegramCorrelation
        {
            Id = Guid.NewGuid(),
            ExecutionId = executionId,
            ProjectModuleId = Guid.NewGuid(),
            TenantDbName = userDbName,
            ChatId = ChatId,
            CreatedAt = DateTime.UtcNow,
            IsResolved = false,
            State = "waiting",
        });
        await coreDb.SaveChangesAsync();

        var factory = new Mock<ITenantDbContextFactory>();
        factory.Setup(f => f.Create(userDbName)).Returns(() => CreateUserDb(userDbName));

        var newExecutionId = Guid.NewGuid();
        var executor = new Mock<IPipelineExecutor>();
        executor.Setup(e => e.AbortFromInteractionAsync(executionId, It.IsAny<UserDbContext>(), userDbName))
            .ReturnsAsync(new ProjectExecution { Id = executionId, ProjectId = projectId, Status = "Cancelled", WorkspacePath = "/tmp/ws" });
        executor.Setup(e => e.CancelQueuedInteractionsAsync(executionId)).Returns(Task.CompletedTask);
        executor.Setup(e => e.ExecuteAsync(projectId, "Tema siguiente", It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new ProjectExecution { Id = newExecutionId, ProjectId = projectId, Status = "Running", WorkspacePath = "/tmp/ws2" });

        var planner = new Mock<IPromptPlannerService>();
        var telegram = new TelegramService(new HttpClient(new OkHandler()));
        var handler = new TelegramUpdateHandler(coreDb, factory.Object, executor.Object, telegram, planner.Object);

        // Act
        await handler.ProcessUpdateAsync(CallbackUpdate("next_execution", ChatId));

        // La ejecución actual se canceló y se lanzó la siguiente temática con su contenido.
        executor.Verify(e => e.AbortFromInteractionAsync(executionId, It.IsAny<UserDbContext>(), userDbName), Times.Once);
        executor.Verify(e => e.ExecuteAsync(projectId, "Tema siguiente", It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);

        // La correlación queda resuelta.
        var corr = await coreDb.TelegramCorrelations.FirstAsync();
        Assert.True(corr.IsResolved);

        // El prompt se marca consumido y apunta a la nueva ejecución.
        await using var verifyDb = CreateUserDb(userDbName);
        var prompt = await verifyDb.PlannedPrompts.FindAsync(promptId);
        Assert.NotNull(prompt);
        Assert.Equal(PlannedPromptStatus.Used, prompt!.Status);
        Assert.Equal(newExecutionId, prompt.ExecutionId);
        Assert.NotNull(prompt.UsedAt);
    }

    [Fact]
    public async Task NextExecution_SinTematicasPendientes_PideNuevaPlanificacion()
    {
        var userDbName = nameof(NextExecution_SinTematicasPendientes_PideNuevaPlanificacion) + "_user";
        var coreDbName = nameof(NextExecution_SinTematicasPendientes_PideNuevaPlanificacion) + "_core";

        var projectId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();

        await using (var seed = CreateUserDb(userDbName))
        {
            seed.MessagingConnections.Add(new MessagingConnection
            {
                Id = connectionId,
                Name = "tg",
                Provider = "telegram",
                BotToken = "token",
                ChatId = ChatId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            seed.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Proj",
                TelegramConnectionId = connectionId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            seed.ProjectExecutions.Add(new ProjectExecution
            {
                Id = executionId,
                ProjectId = projectId,
                Status = "WaitingForInput",
                WorkspacePath = "/tmp/ws",
                CreatedAt = DateTime.UtcNow,
            });
            // No PlannedPrompts pending on purpose.
            await seed.SaveChangesAsync();
        }

        var coreDb = CreateCoreDb(coreDbName);
        coreDb.TelegramCorrelations.Add(new TelegramCorrelation
        {
            Id = Guid.NewGuid(),
            ExecutionId = executionId,
            ProjectModuleId = Guid.NewGuid(),
            TenantDbName = userDbName,
            ChatId = ChatId,
            CreatedAt = DateTime.UtcNow,
            IsResolved = false,
            State = "waiting",
        });
        await coreDb.SaveChangesAsync();

        var factory = new Mock<ITenantDbContextFactory>();
        factory.Setup(f => f.Create(userDbName)).Returns(() => CreateUserDb(userDbName));

        var executor = new Mock<IPipelineExecutor>();
        executor.Setup(e => e.AbortFromInteractionAsync(executionId, It.IsAny<UserDbContext>(), userDbName))
            .ReturnsAsync(new ProjectExecution { Id = executionId, ProjectId = projectId, Status = "Cancelled", WorkspacePath = "/tmp/ws" });
        executor.Setup(e => e.CancelQueuedInteractionsAsync(executionId)).Returns(Task.CompletedTask);

        var planner = new Mock<IPromptPlannerService>();
        var telegram = new TelegramService(new HttpClient(new OkHandler()));
        var handler = new TelegramUpdateHandler(coreDb, factory.Object, executor.Object, telegram, planner.Object);

        // Act
        await handler.ProcessUpdateAsync(CallbackUpdate("next_execution", ChatId));

        // La ejecución actual se canceló pero no se lanzó ninguna nueva (cola vacía).
        executor.Verify(e => e.AbortFromInteractionAsync(executionId, It.IsAny<UserDbContext>(), userDbName), Times.Once);
        executor.Verify(e => e.ExecuteAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<UserDbContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);

        // Se abrió una petición de planificación para el proyecto.
        var planningReq = await coreDb.TelegramCorrelations
            .FirstOrDefaultAsync(c => c.State == "awaiting_planning" && c.ProjectId == projectId && !c.IsResolved);
        Assert.NotNull(planningReq);

        // La correlación original queda resuelta.
        var original = await coreDb.TelegramCorrelations.FirstAsync(c => c.ExecutionId == executionId);
        Assert.True(original.IsResolved);
    }
}
