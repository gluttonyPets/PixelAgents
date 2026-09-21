using Server.Services.Telegram;
using Xunit;

namespace Server.Tests.HilosTelegram;

/// <summary>
/// El callback_data de los botones lleva el token de la interaccion a la que pertenecen.
/// Sin el, pulsar "Continuar" en un mensaje antiguo resolvia la interaccion mas vieja del
/// chat (aunque fuera de otro pipeline). Telegram limita callback_data a 64 bytes.
/// </summary>
public class TelegramCallbackTests
{
    [Fact]
    public void Build_Y_Parse_ConservanAccionYToken()
    {
        var data = TelegramCallback.Build(TelegramCallback.Continue, "A3F2B1C9");

        var parsed = TelegramCallback.Parse(data);

        Assert.Equal(TelegramCallback.Continue, parsed.Action);
        Assert.Equal("A3F2B1C9", parsed.Token);
        Assert.Null(parsed.Payload);
    }

    [Fact]
    public void Build_ConPayload_LoDevuelveEntero()
    {
        // Los ids de modelo pueden llevar guiones y puntos; el separador es '|'.
        var data = TelegramCallback.Build(TelegramCallback.EditModel, "A3F2B1C9", "gemini-2.5-flash-image-preview");

        var parsed = TelegramCallback.Parse(data);

        Assert.Equal(TelegramCallback.EditModel, parsed.Action);
        Assert.Equal("A3F2B1C9", parsed.Token);
        Assert.Equal("gemini-2.5-flash-image-preview", parsed.Payload);
    }

    [Fact]
    public void Parse_CallbackAntiguoSinSeparador_SigueFuncionando()
    {
        // Mensajes enviados antes de este cambio: se interpretan como accion sin token.
        var parsed = TelegramCallback.Parse("continue");

        Assert.Equal("continue", parsed.Action);
        Assert.Null(parsed.Token);
    }

    [Fact]
    public void Parse_Vacio_NoRevienta()
    {
        var parsed = TelegramCallback.Parse("");

        Assert.Equal("", parsed.Action);
        Assert.Null(parsed.Token);
    }

    [Fact]
    public void Build_CabeEnElLimiteDe64BytesDeTelegram()
    {
        var token = TelegramCallback.NewToken();
        var data = TelegramCallback.Build(TelegramCallback.EditModel, token, "gemini-2.5-flash-image-preview");

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(data) <= 64, $"callback_data demasiado largo: {data}");
    }

    [Fact]
    public void NewToken_EsCortoYUnico()
    {
        var a = TelegramCallback.NewToken();
        var b = TelegramCallback.NewToken();

        Assert.Equal(8, a.Length);
        Assert.NotEqual(a, b);
    }
}
