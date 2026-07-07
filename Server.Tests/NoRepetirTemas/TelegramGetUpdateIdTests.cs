using System.Text.Json;
using Server.Services.Telegram;
using Xunit;

namespace Server.Tests.NoRepetirTemas;

/// <summary>
/// Tests para <see cref="TelegramService.GetUpdateId"/>, base de la deduplicacion de updates
/// que evita procesar dos veces el mismo update de Telegram (y reenviar la interaccion).
/// </summary>
public class TelegramGetUpdateIdTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void GetUpdateId_ConUpdateId_LoDevuelve()
    {
        var body = Parse(@"{ ""update_id"": 123456789, ""message"": { ""text"": ""hola"" } }");

        Assert.Equal(123456789L, TelegramService.GetUpdateId(body));
    }

    [Fact]
    public void GetUpdateId_CallbackQuery_TambienLoDevuelve()
    {
        var body = Parse(@"{ ""update_id"": 42, ""callback_query"": { ""id"": ""abc"", ""data"": ""continue"" } }");

        Assert.Equal(42L, TelegramService.GetUpdateId(body));
    }

    [Fact]
    public void GetUpdateId_SinUpdateId_DevuelveNull()
    {
        var body = Parse(@"{ ""message"": { ""text"": ""hola"" } }");

        Assert.Null(TelegramService.GetUpdateId(body));
    }

    [Fact]
    public void GetUpdateId_UpdateIdNoNumerico_DevuelveNull()
    {
        var body = Parse(@"{ ""update_id"": ""no-es-numero"" }");

        Assert.Null(TelegramService.GetUpdateId(body));
    }
}
