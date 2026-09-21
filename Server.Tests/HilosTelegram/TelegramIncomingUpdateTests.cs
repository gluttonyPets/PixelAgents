using System.Text.Json;
using Server.Services.Telegram;
using Xunit;

namespace Server.Tests.HilosTelegram;

/// <summary>
/// El parseo de updates conserva las claves que permiten saber a QUE pregunta responde el
/// usuario: el mensaje citado (reply_to_message) y el hilo (message_thread_id).
/// </summary>
public class TelegramIncomingUpdateTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void MensajeCitado_DevuelveElMessageIdCitado()
    {
        var body = Parse(@"{
            ""update_id"": 1,
            ""message"": {
                ""message_id"": 77,
                ""text"": ""me gusta"",
                ""date"": 1700000000,
                ""chat"": { ""id"": 12345 },
                ""reply_to_message"": { ""message_id"": 42 }
            }
        }");

        var update = TelegramService.ParseIncomingUpdate(body);

        Assert.Equal("me gusta", update.Text);
        Assert.Equal("12345", update.ChatId);
        Assert.Equal(77, update.MessageId);
        Assert.Equal(42, update.ReplyToMessageId);
    }

    [Fact]
    public void MensajeEnHilo_DevuelveElThreadId()
    {
        var body = Parse(@"{
            ""update_id"": 2,
            ""message"": {
                ""message_id"": 78,
                ""text"": ""ok"",
                ""date"": 1700000000,
                ""chat"": { ""id"": 12345 },
                ""message_thread_id"": 900
            }
        }");

        var update = TelegramService.ParseIncomingUpdate(body);

        Assert.Equal(900, update.MessageThreadId);
    }

    [Fact]
    public void EnUnHilo_LaCitaAlMensajeRaizNoCuentaComoCita()
    {
        // Telegram marca el primer mensaje del topic como reply_to_message aunque el usuario
        // no haya citado nada: tomarlo por una cita real correlacionaria mal.
        var body = Parse(@"{
            ""update_id"": 3,
            ""message"": {
                ""message_id"": 79,
                ""text"": ""ok"",
                ""date"": 1700000000,
                ""chat"": { ""id"": 12345 },
                ""message_thread_id"": 900,
                ""reply_to_message"": { ""message_id"": 900 }
            }
        }");

        var update = TelegramService.ParseIncomingUpdate(body);

        Assert.Equal(900, update.MessageThreadId);
        Assert.Null(update.ReplyToMessageId);
    }

    [Fact]
    public void Callback_DevuelveElMensajeDelBotonComoReferencia()
    {
        var body = Parse(@"{
            ""update_id"": 4,
            ""callback_query"": {
                ""id"": ""cbq1"",
                ""data"": ""cont|A3F2B1C9"",
                ""message"": { ""message_id"": 55, ""message_thread_id"": 900, ""chat"": { ""id"": 12345 } }
            }
        }");

        var update = TelegramService.ParseIncomingUpdate(body);

        Assert.Equal("cont|A3F2B1C9", update.Text);
        Assert.Equal("cbq1", update.CallbackQueryId);
        Assert.Equal(55, update.MessageId);
        Assert.Equal(55, update.ReplyToMessageId);
        Assert.Equal(900, update.MessageThreadId);
    }

    [Fact]
    public void UpdateSinMensaje_DevuelveTodoVacio()
    {
        var update = TelegramService.ParseIncomingUpdate(Parse(@"{ ""update_id"": 5 }"));

        Assert.Null(update.Text);
        Assert.Null(update.ChatId);
        Assert.Null(update.MessageThreadId);
    }
}
