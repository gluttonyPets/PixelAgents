using System.Text.Json;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.TextoAVoz;

/// <summary>
/// Peticion de texto a voz de OpenAI. Lo que se rompe al tocarla es mandar un
/// parametro al modelo que no lo acepta: tts-1 rechaza <c>instructions</c> y
/// gpt-4o-mini-tts ignora <c>speed</c>.
/// </summary>
public class OpenAiSpeechTests
{
    [Fact]
    public void Tts1_MandaVelocidadYNoInstrucciones()
    {
        var json = Parse(OpenAiSpeech.BuildRequestJson("tts-1", "Hola", Config(
            ("voice", "nova"), ("speed", 1.25), (OpenAiSpeech.InstructionsKey, "Tono calido"))));

        Assert.Equal("tts-1", json.GetProperty("model").GetString());
        Assert.Equal("Hola", json.GetProperty("input").GetString());
        Assert.Equal("nova", json.GetProperty("voice").GetString());
        Assert.Equal(1.25, json.GetProperty("speed").GetDouble());
        Assert.False(json.TryGetProperty("instructions", out _));
    }

    [Fact]
    public void MiniTts_LaVelocidadPasaALasInstrucciones()
    {
        var json = Parse(OpenAiSpeech.BuildRequestJson("gpt-4o-mini-tts", "Hola", Config(
            ("speed", 1.25), (OpenAiSpeech.InstructionsKey, "Voz en off de anuncio, acento de Espana."))));

        Assert.False(json.TryGetProperty("speed", out _));
        var instructions = json.GetProperty("instructions").GetString()!;
        Assert.StartsWith("Voz en off de anuncio, acento de Espana.", instructions);
        Assert.Contains("mas rapido", instructions);
        Assert.Contains("1.25", instructions);
    }

    [Fact]
    public void SinConfiguracion_VozPorDefectoYNadaMas()
    {
        var json = Parse(OpenAiSpeech.BuildRequestJson("gpt-4o-mini-tts", "Hola", Config()));

        Assert.Equal(OpenAiSpeech.DefaultVoice, json.GetProperty("voice").GetString());
        Assert.Equal("mp3", json.GetProperty("response_format").GetString());
        Assert.False(json.TryGetProperty("instructions", out _));
        Assert.False(json.TryGetProperty("speed", out _));
    }

    [Fact]
    public void VelocidadNormal_NoSeManda()
    {
        var json = Parse(OpenAiSpeech.BuildRequestJson("tts-1", "Hola", Config(("speed", 1.0))));

        Assert.False(json.TryGetProperty("speed", out _));
    }

    [Fact]
    public void VelocidadComoJsonElement_SeLee()
    {
        // Asi llega del executor cuando la configuracion viene de la base de datos.
        using var doc = JsonDocument.Parse("{\"speed\":0.75}");
        var config = new Dictionary<string, object> { ["speed"] = doc.RootElement.GetProperty("speed").Clone() };

        var json = Parse(OpenAiSpeech.BuildRequestJson("tts-1-hd", "Hola", config));

        Assert.Equal(0.75, json.GetProperty("speed").GetDouble());
    }

    [Fact]
    public void Coste_Tts1EsPorCaracter()
    {
        // $15 por millon de caracteres.
        Assert.Equal(0.015m, OpenAiSpeech.EstimateCost("tts-1", 1000));
        Assert.Equal(0.030m, OpenAiSpeech.EstimateCost("tts-1-hd", 1000));
    }

    [Fact]
    public void Coste_MiniTtsRondaElCentimoYMedioPorMinuto()
    {
        // 900 caracteres son un minuto de locucion estimado: ~$0.015.
        var cost = OpenAiSpeech.EstimateCost("gpt-4o-mini-tts", 900);
        Assert.InRange(cost, 0.014m, 0.017m);
    }

    private static Dictionary<string, object> Config(params (string Key, object Value)[] values)
    {
        var config = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values) config[k] = v;
        return config;
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
