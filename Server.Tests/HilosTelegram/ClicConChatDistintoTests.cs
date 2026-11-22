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
/// La conexion de Telegram puede guardarse como "@canal", pero Telegram manda siempre el id
/// numerico del chat al pulsar un boton. Comparando chats tal cual, el "Continuar" no
/// encontraba su pregunta, se descartaba en silencio y la ejecucion se quedaba esperando.
/// </summary>
public class ClicConChatDistintoTests
{
    private const string UpdateChatId = "-1001234567890";
    private const string Token = "DDDD4444";

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":1,\"id\":-1009999}}"),
            });
    }

    private static UserDbContext CreateUserDb(string name) =>
        new(new DbContextOptionsBuilder<UserDbContext>().UseInMemoryDatabase(name).Options);

    private static JsonElement ContinueUpdate(long updateId) =>
        JsonDocument.Parse($$"""
        {
            "update_id": {{updateId}},
            "callback_query": {
                "id": "cbq{{updateId}}",
                "data": "{{TelegramCallback.Build(TelegramCallback.Continue, Token)}}",
                "message": { "message_id": 10, "chat": { "id": {{UpdateChatId}} } }
            }
        }
        """).RootElement;

    [Fact]
    public async Task BotonConToken_ReanudaAunqueLaCorrelacionGuardeElChatComoArroba()
    {
        var name = nameof(BotonConToken_ReanudaAunqueLaCorrelacionGuardeElChatComoArroba);
        var userDbName = name + "_user";
        var execId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();

        await using (var seed = CreateUserDb(userDbName))
        {
            seed.MessagingConnections.Add(new MessagingConnection
            {
                Id = connectionId, Name = "tg", Provider = "telegram", BotToken = "token", ChatId = "@mi_canal",
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

        var coreDb = new CoreDbContext(new DbContextOptionsBuilder<CoreDbContext>().UseInMemoryDatabase(name + "_core").Options);
        coreDb.TelegramCorrelations.Add(new TelegramCorrelation
        {
            Id = Guid.NewGuid(), ExecutionId = execId, ProjectModuleId = Guid.NewGuid(),
            TenantDbName = userDbName, ChatId = "@mi_canal", CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            IsResolved = false, State = "waiting", Token = Token,
        });
        await coreDb.SaveChangesAsync();

        var factory = new Mock<ITenantDbContextFactory>();
        factory.Setup(f => f.Create(userDbName)).Returns(() => CreateUserDb(userDbName));
        var executor = new Mock<IPipelineExecutor>();
        executor.Setup(e => e.ResumeFromInteractionAsync(execId, "continue", It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectExecution { Id = execId, Status = "Completed" });

        var handler = new TelegramUpdateHandler(coreDb, factory.Object, executor.Object,
            new TelegramService(new HttpClient(new OkHandler())),
            new Mock<IPromptPlannerService>().Object, new Mock<ILearningAnalysisService>().Object);

        await handler.ProcessUpdateAsync(ContinueUpdate(1));

        executor.Verify(e => e.ResumeFromInteractionAsync(
            execId, "continue", It.IsAny<UserDbContext>(), userDbName, It.IsAny<CancellationToken>()), Times.Once);
        var correlation = await coreDb.TelegramCorrelations.SingleAsync();
        Assert.True(correlation.IsResolved);
        // Se queda con el id numerico para que los textos sueltos tambien casen.
        Assert.Equal(UpdateChatId, correlation.ChatId);
    }

    [Fact]
    public async Task ChatIdNumerico_NoLlamaATelegram()
    {
        var handler = new CountingHandler();
        var telegram = new TelegramService(new HttpClient(handler));

        Assert.Equal("-100555", await telegram.ResolveNumericChatIdAsync("bot", " -100555 "));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ChatIdConArroba_SeResuelveConGetChat()
    {
        var handler = new CountingHandler();
        var telegram = new TelegramService(new HttpClient(handler));

        Assert.Equal("-1009999", await telegram.ResolveNumericChatIdAsync("bot-resolve", "@otro_canal"));
        Assert.Equal(1, handler.Calls);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":{\"id\":-1009999,\"type\":\"channel\"}}"),
            });
        }
    }
}
