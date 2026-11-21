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

namespace Server.Tests.HilosTelegram;

/// <summary>
/// "Continuar" desde Telegram: el pipeline reanudado ya no corre dentro del procesado del
/// update (bloqueaba el resto de botones mientras duraba) y, si la reanudacion falla antes
/// de arrancar, la pregunta se reabre en vez de dejar la ejecucion en "Esperando" sin botones.
/// </summary>
public class ContinuarEnSegundoPlanoTests
{
    private const string ChatId = "777";
    private const string Token = "CCCC3333";

    private static CoreDbContext CreateCoreDb(string name) =>
        new(new DbContextOptionsBuilder<CoreDbContext>().UseInMemoryDatabase(name).Options);

    private static UserDbContext CreateUserDb(string name) =>
        new(new DbContextOptionsBuilder<UserDbContext>().UseInMemoryDatabase(name).Options);

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":42}}"),
            });
    }

    /// <summary>Guarda los trabajos sin ejecutarlos, como si corrieran en segundo plano.</summary>
    private sealed class DeferredDispatcher : ITelegramWorkDispatcher
    {
        public List<Func<TelegramWork, Task>> Pending { get; } = [];

        public Task DispatchAsync(string description, Func<TelegramWork, Task> work)
        {
            Pending.Add(work);
            return Task.CompletedTask;
        }
    }

    private static JsonElement ContinueUpdate(long updateId) =>
        JsonDocument.Parse($$"""
        {
            "update_id": {{updateId}},
            "callback_query": {
                "id": "cbq{{updateId}}",
                "data": "{{TelegramCallback.Build(TelegramCallback.Continue, Token)}}",
                "message": { "message_id": 10, "chat": { "id": {{ChatId}} } }
            }
        }
        """).RootElement;

    private static async Task<(Guid ExecId, string UserDbName, CoreDbContext CoreDb)> SeedAsync(string testName)
    {
        var userDbName = testName + "_user";
        var projectId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var execId = Guid.NewGuid();

        await using (var seed = CreateUserDb(userDbName))
        {
            seed.MessagingConnections.Add(new MessagingConnection
            {
                Id = connectionId, Name = "tg", Provider = "telegram", BotToken = "token", ChatId = ChatId,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            seed.Projects.Add(new Project
            {
                Id = projectId, Name = "Proj", TelegramConnectionId = connectionId,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            seed.ProjectExecutions.Add(new ProjectExecution
            {
                Id = execId, ProjectId = projectId, Status = "WaitingForInput",
                WorkspacePath = "/tmp/ws", CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var coreDb = CreateCoreDb(testName + "_core");
        coreDb.TelegramCorrelations.Add(new TelegramCorrelation
        {
            Id = Guid.NewGuid(), ExecutionId = execId, ProjectModuleId = Guid.NewGuid(),
            TenantDbName = userDbName, ChatId = ChatId, CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            IsResolved = false, State = "waiting", Token = Token, Label = "Proj · Revision",
        });
        await coreDb.SaveChangesAsync();
        return (execId, userDbName, coreDb);
    }

    private static Mock<ITenantDbContextFactory> Factory(string userDbName)
    {
        var factory = new Mock<ITenantDbContextFactory>();
        factory.Setup(f => f.Create(userDbName)).Returns(() => CreateUserDb(userDbName));
        return factory;
    }

    private static TelegramUpdateHandler Handler(
        CoreDbContext coreDb, Mock<ITenantDbContextFactory> factory, Mock<IPipelineExecutor> executor,
        ITelegramWorkDispatcher? dispatcher = null) =>
        new(coreDb, factory.Object, executor.Object, new TelegramService(new HttpClient(new OkHandler())),
            new Mock<IPromptPlannerService>().Object, new Mock<ILearningAnalysisService>().Object, dispatcher);

    [Fact]
    public async Task Continuar_NoEsperaAlPipeline_YCierraLaPreguntaAlMomento()
    {
        var (execId, userDbName, coreDb) = await SeedAsync(nameof(Continuar_NoEsperaAlPipeline_YCierraLaPreguntaAlMomento));
        var executor = new Mock<IPipelineExecutor>();
        var dispatcher = new DeferredDispatcher();
        var factory = Factory(userDbName);

        await Handler(coreDb, factory, executor, dispatcher).ProcessUpdateAsync(ContinueUpdate(1));

        // El update se ha procesado sin ejecutar el pipeline: queda programado aparte.
        executor.Verify(e => e.ResumeFromInteractionAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<UserDbContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Single(dispatcher.Pending);
        Assert.True((await coreDb.TelegramCorrelations.SingleAsync()).IsResolved);

        // Cuando corre el trabajo, reanuda esa ejecucion y envia lo que hubiera en cola.
        executor.Setup(e => e.ResumeFromInteractionAsync(execId, "continue", It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectExecution { Id = execId, Status = "Completed" });
        await dispatcher.Pending[0](new TelegramWork(coreDb, factory.Object, executor.Object,
            new TelegramService(new HttpClient(new OkHandler()))));

        executor.Verify(e => e.ResumeFromInteractionAsync(
            execId, "continue", It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>()), Times.Once);
        executor.Verify(e => e.SendNextQueuedInteractionAsync(execId, ChatId), Times.Once);
    }

    [Fact]
    public async Task ReanudacionQueFallaAntesDeArrancar_ReabreLaPregunta()
    {
        var (execId, userDbName, coreDb) = await SeedAsync(nameof(ReanudacionQueFallaAntesDeArrancar_ReabreLaPregunta));
        var executor = new Mock<IPipelineExecutor>();
        // Falla sin tocar la ejecucion: sigue en WaitingForInput.
        executor.Setup(e => e.ResumeFromInteractionAsync(execId, It.IsAny<string>(), It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Modulo pausado no encontrado"));

        await Handler(coreDb, Factory(userDbName), executor).ProcessUpdateAsync(ContinueUpdate(2));

        var correlation = await coreDb.TelegramCorrelations.SingleAsync();
        Assert.False(correlation.IsResolved);
        Assert.Equal("waiting", correlation.State);
        Assert.Equal(42, correlation.BotMessageId); // se reenviaron los botones
        executor.Verify(e => e.SendNextQueuedInteractionAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ReanudacionQueFallaAMitad_NoReabreLaPregunta()
    {
        var (execId, userDbName, coreDb) = await SeedAsync(nameof(ReanudacionQueFallaAMitad_NoReabreLaPregunta));
        var executor = new Mock<IPipelineExecutor>();
        executor.Setup(e => e.ResumeFromInteractionAsync(execId, It.IsAny<string>(), It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                // El pipeline ya habia arrancado cuando fallo.
                await using var db = CreateUserDb(userDbName);
                var exec = await db.ProjectExecutions.FirstAsync(e => e.Id == execId);
                exec.Status = "Failed";
                await db.SaveChangesAsync();
                throw new InvalidOperationException("boom");
            });

        await Handler(coreDb, Factory(userDbName), executor).ProcessUpdateAsync(ContinueUpdate(3));

        Assert.True((await coreDb.TelegramCorrelations.SingleAsync()).IsResolved);
    }
}
