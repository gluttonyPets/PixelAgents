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
/// Dos pipelines pueden estar esperando respuesta en el mismo chat a la vez. Antes, cualquier
/// mensaje entrante se aplicaba a la correlacion mas antigua del chat, asi que la respuesta a
/// la pregunta B reanudaba la ejecucion A. Estos tests fijan la correlacion explicita:
/// token del boton, mensaje citado, hilo y, si no hay pista, preguntar en vez de adivinar.
/// </summary>
public class InteraccionesSolapadasTests
{
    private const string ChatId = "12345";
    private const string TokenA = "AAAA1111";
    private const string TokenB = "BBBB2222";

    private static CoreDbContext CreateCoreDb(string name) =>
        new(new DbContextOptionsBuilder<CoreDbContext>().UseInMemoryDatabase(name).Options);

    private static UserDbContext CreateUserDb(string name) =>
        new(new DbContextOptionsBuilder<UserDbContext>().UseInMemoryDatabase(name).Options);

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":1}}"),
            });
    }

    private static JsonElement CallbackUpdate(string data, long updateId, long messageId = 10) =>
        JsonDocument.Parse($$"""
        {
            "update_id": {{updateId}},
            "callback_query": {
                "id": "cbq{{updateId}}",
                "data": "{{data}}",
                "message": { "message_id": {{messageId}}, "chat": { "id": {{ChatId}} } }
            }
        }
        """).RootElement;

    private static JsonElement TextUpdate(string text, long updateId, long? replyTo = null, long? threadId = null)
    {
        var reply = replyTo is null ? "" : $@", ""reply_to_message"": {{ ""message_id"": {replyTo} }}";
        var thread = threadId is null ? "" : $@", ""message_thread_id"": {threadId}";
        var date = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return JsonDocument.Parse($$"""
        {
            "update_id": {{updateId}},
            "message": {
                "message_id": {{updateId + 500}},
                "text": "{{text}}",
                "date": {{date}},
                "chat": { "id": {{ChatId}} }{{reply}}{{thread}}
            }
        }
        """).RootElement;
    }

    /// <summary>Dos ejecuciones esperando en el mismo chat, con sus dos correlaciones abiertas.</summary>
    private static async Task<(Guid ExecA, Guid ExecB, string UserDbName, CoreDbContext CoreDb)> SeedDosEjecucionesAsync(
        string testName, long? botMessageIdA = null, long? botMessageIdB = null,
        long? threadA = null, long? threadB = null)
    {
        var userDbName = testName + "_user";
        var coreDbName = testName + "_core";

        var projectId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var execA = Guid.NewGuid();
        var execB = Guid.NewGuid();

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
            foreach (var id in new[] { execA, execB })
            {
                seed.ProjectExecutions.Add(new ProjectExecution
                {
                    Id = id,
                    ProjectId = projectId,
                    Status = "WaitingForInput",
                    WorkspacePath = "/tmp/ws",
                    CreatedAt = DateTime.UtcNow,
                });
            }
            await seed.SaveChangesAsync();
        }

        var coreDb = CreateCoreDb(coreDbName);
        coreDb.TelegramCorrelations.Add(new TelegramCorrelation
        {
            Id = Guid.NewGuid(),
            ExecutionId = execA,
            ProjectModuleId = Guid.NewGuid(),
            TenantDbName = userDbName,
            ChatId = ChatId,
            // A es la mas antigua: es la que se llevaba todas las respuestas.
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
            IsResolved = false,
            State = "waiting",
            Token = TokenA,
            Label = "Proj · Revision A",
            BotMessageId = botMessageIdA,
            MessageThreadId = threadA,
        });
        coreDb.TelegramCorrelations.Add(new TelegramCorrelation
        {
            Id = Guid.NewGuid(),
            ExecutionId = execB,
            ProjectModuleId = Guid.NewGuid(),
            TenantDbName = userDbName,
            ChatId = ChatId,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            IsResolved = false,
            State = "waiting",
            Token = TokenB,
            Label = "Proj · Revision B",
            BotMessageId = botMessageIdB,
            MessageThreadId = threadB,
        });
        await coreDb.SaveChangesAsync();

        return (execA, execB, userDbName, coreDb);
    }

    private static (TelegramUpdateHandler Handler, Mock<IPipelineExecutor> Executor) BuildHandler(
        CoreDbContext coreDb, string userDbName)
    {
        var factory = new Mock<ITenantDbContextFactory>();
        factory.Setup(f => f.Create(userDbName)).Returns(() => CreateUserDb(userDbName));

        var executor = new Mock<IPipelineExecutor>();
        executor.Setup(e => e.ResumeFromInteractionAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectExecution { Id = Guid.NewGuid(), Status = "Running", WorkspacePath = "/tmp/ws" });
        executor.Setup(e => e.SendNextQueuedInteractionAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var telegram = new TelegramService(new HttpClient(new OkHandler()));
        var handler = new TelegramUpdateHandler(
            coreDb, factory.Object, executor.Object, telegram,
            new Mock<IPromptPlannerService>().Object, new Mock<ILearningAnalysisService>().Object);

        return (handler, executor);
    }

    [Fact]
    public async Task BotonConToken_ReanudaSuEjecucionYNoLaMasAntigua()
    {
        var (execA, execB, userDbName, coreDb) = await SeedDosEjecucionesAsync(
            nameof(BotonConToken_ReanudaSuEjecucionYNoLaMasAntigua));
        var (handler, executor) = BuildHandler(coreDb, userDbName);

        await handler.ProcessUpdateAsync(CallbackUpdate(TelegramCallback.Build(TelegramCallback.Continue, TokenB), 1));

        executor.Verify(e => e.ResumeFromInteractionAsync(
            execB, "continue", It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>()), Times.Once);
        executor.Verify(e => e.ResumeFromInteractionAsync(
            execA, It.IsAny<string>(), It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>()), Times.Never);

        var corrA = await coreDb.TelegramCorrelations.FirstAsync(c => c.ExecutionId == execA);
        var corrB = await coreDb.TelegramCorrelations.FirstAsync(c => c.ExecutionId == execB);
        Assert.False(corrA.IsResolved);
        Assert.True(corrB.IsResolved);
    }

    [Fact]
    public async Task RespuestaCitada_VaALaInteraccionCitada()
    {
        var (execA, execB, userDbName, coreDb) = await SeedDosEjecucionesAsync(
            nameof(RespuestaCitada_VaALaInteraccionCitada), botMessageIdA: 100, botMessageIdB: 200);
        var (handler, executor) = BuildHandler(coreDb, userDbName);

        // El usuario responde citando el mensaje de la interaccion B (message_id 200).
        await handler.ProcessUpdateAsync(TextUpdate("cambia el titulo", 2, replyTo: 200));

        executor.Verify(e => e.ResumeFromInteractionAsync(
            execB, "cambia el titulo", It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>()), Times.Once);
        executor.Verify(e => e.ResumeFromInteractionAsync(
            execA, It.IsAny<string>(), It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MensajeEnUnHilo_VaALaEjecucionDeEseHilo()
    {
        var (execA, execB, userDbName, coreDb) = await SeedDosEjecucionesAsync(
            nameof(MensajeEnUnHilo_VaALaEjecucionDeEseHilo), threadA: 900, threadB: 901);
        var (handler, executor) = BuildHandler(coreDb, userDbName);

        await handler.ProcessUpdateAsync(TextUpdate("adelante", 3, threadId: 901));

        executor.Verify(e => e.ResumeFromInteractionAsync(
            execB, "adelante", It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>()), Times.Once);
        executor.Verify(e => e.ResumeFromInteractionAsync(
            execA, It.IsAny<string>(), It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TextoAmbiguo_NoReanudaNadaYGuardaElMensajePendiente()
    {
        var (execA, execB, userDbName, coreDb) = await SeedDosEjecucionesAsync(
            nameof(TextoAmbiguo_NoReanudaNadaYGuardaElMensajePendiente));
        var (handler, executor) = BuildHandler(coreDb, userDbName);

        await handler.ProcessUpdateAsync(TextUpdate("vale", 4));

        executor.Verify(e => e.ResumeFromInteractionAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>()), Times.Never);

        var pending = await coreDb.TelegramPendingReplies.FirstOrDefaultAsync(r => r.ChatId == ChatId);
        Assert.NotNull(pending);
        Assert.Equal("vale", pending!.Text);
    }

    [Fact]
    public async Task TrasElegirInteraccion_ElTextoPendienteSeAplicaAEsaEjecucion()
    {
        var (execA, execB, userDbName, coreDb) = await SeedDosEjecucionesAsync(
            nameof(TrasElegirInteraccion_ElTextoPendienteSeAplicaAEsaEjecucion));
        var (handler, executor) = BuildHandler(coreDb, userDbName);

        await handler.ProcessUpdateAsync(TextUpdate("vale", 5));
        await handler.ProcessUpdateAsync(CallbackUpdate(TelegramCallback.Build(TelegramCallback.Pick, TokenA), 6));

        executor.Verify(e => e.ResumeFromInteractionAsync(
            execA, "vale", It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>()), Times.Once);

        // El pendiente se consume: no puede volver a aplicarse a otra interaccion.
        Assert.False(await coreDb.TelegramPendingReplies.AnyAsync(r => r.ChatId == ChatId));
    }

    [Fact]
    public async Task BotonDeUnaInteraccionYaCerrada_NoTocaLaOtra()
    {
        var (execA, execB, userDbName, coreDb) = await SeedDosEjecucionesAsync(
            nameof(BotonDeUnaInteraccionYaCerrada_NoTocaLaOtra));

        var corrB = await coreDb.TelegramCorrelations.FirstAsync(c => c.ExecutionId == execB);
        corrB.IsResolved = true;
        await coreDb.SaveChangesAsync();

        var (handler, executor) = BuildHandler(coreDb, userDbName);

        // Se pulsa un boton viejo de B, que ya esta cerrada: antes esto reanudaba A.
        await handler.ProcessUpdateAsync(CallbackUpdate(TelegramCallback.Build(TelegramCallback.Continue, TokenB), 7));

        executor.Verify(e => e.ResumeFromInteractionAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>()), Times.Never);

        var corrA = await coreDb.TelegramCorrelations.FirstAsync(c => c.ExecutionId == execA);
        Assert.False(corrA.IsResolved);
    }

    [Fact]
    public async Task ConUnaSolaInteraccionAbierta_ElTextoSueltoSigueFuncionando()
    {
        var (execA, execB, userDbName, coreDb) = await SeedDosEjecucionesAsync(
            nameof(ConUnaSolaInteraccionAbierta_ElTextoSueltoSigueFuncionando));

        var corrB = await coreDb.TelegramCorrelations.FirstAsync(c => c.ExecutionId == execB);
        corrB.IsResolved = true;
        await coreDb.SaveChangesAsync();

        var (handler, executor) = BuildHandler(coreDb, userDbName);

        await handler.ProcessUpdateAsync(TextUpdate("publica", 8));

        executor.Verify(e => e.ResumeFromInteractionAsync(
            execA, "publica", It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(await coreDb.TelegramPendingReplies.AnyAsync(r => r.ChatId == ChatId));
    }
}
