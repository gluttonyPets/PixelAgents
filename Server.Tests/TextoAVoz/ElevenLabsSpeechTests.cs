using System.Text.Json;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.TextoAVoz;

/// <summary>
/// Peticion de texto a voz de ElevenLabs. Lo que se rompe al tocarla: la voz
/// tiene que ir en la ruta (es un id), y los ajustes fuera de rango los rechaza
/// la API con un 400 que tumba la toma entera.
/// </summary>
public class ElevenLabsSpeechTests
{
    [Fact]
    public void LaVozVaEnLaRutaYLaSalidaEsMp3()
    {
        var url = ElevenLabsSpeech.BuildRequestUrl(Config(("voice", "abc123")));

        Assert.Equal("https://api.elevenlabs.io/v1/text-to-speech/abc123?output_format=mp3_44100_128", url);
    }

    [Fact]
    public void SinVozUsaLaPredeterminada()
    {
        var url = ElevenLabsSpeech.BuildRequestUrl(Config());

        Assert.Contains("/text-to-speech/" + ElevenLabsSpeech.DefaultVoice + "?", url);
    }

    [Fact]
    public void MandaTextoModeloYAjustesDeVoz()
    {
        var json = Parse(ElevenLabsSpeech.BuildRequestJson("eleven_v4", "Hola", Config(
            ("voice", "abc123"), ("speed", 1.1), (ElevenLabsSpeech.StabilityKey, 0.3),
            (ElevenLabsSpeech.SimilarityKey, 0.8), (ElevenLabsSpeech.StyleKey, 0.2))));

        Assert.Equal("Hola", json.GetProperty("text").GetString());
        Assert.Equal("eleven_v4", json.GetProperty("model_id").GetString());
        var settings = json.GetProperty("voice_settings");
        Assert.Equal(1.1, settings.GetProperty("speed").GetDouble());
        Assert.Equal(0.3, settings.GetProperty("stability").GetDouble());
        Assert.Equal(0.8, settings.GetProperty("similarity_boost").GetDouble());
        Assert.Equal(0.2, settings.GetProperty("style").GetDouble());
    }

    [Fact]
    public void SinAjustesNoMandaVoiceSettings()
    {
        // Sin ajustes manda la voz con lo que tenga guardado en ElevenLabs.
        var json = Parse(ElevenLabsSpeech.BuildRequestJson("eleven_v4", "Hola", Config(("speed", 1.0))));

        Assert.False(json.TryGetProperty("voice_settings", out _));
    }

    [Fact]
    public void LaVelocidadSeAcotaAlRangoDeLaApi()
    {
        var rapida = Parse(ElevenLabsSpeech.BuildRequestJson("eleven_v4", "Hola", Config(("speed", 2.0))));
        var lenta = Parse(ElevenLabsSpeech.BuildRequestJson("eleven_v4", "Hola", Config(("speed", 0.25))));

        Assert.Equal(ElevenLabsSpeech.MaxSpeed, rapida.GetProperty("voice_settings").GetProperty("speed").GetDouble());
        Assert.Equal(ElevenLabsSpeech.MinSpeed, lenta.GetProperty("voice_settings").GetProperty("speed").GetDouble());
    }

    [Fact]
    public void ElevenV3NoRecibeVelocidad()
    {
        var json = Parse(ElevenLabsSpeech.BuildRequestJson("eleven_v3", "Hola", Config(("speed", 1.1))));

        Assert.False(json.TryGetProperty("voice_settings", out _));
    }

    [Fact]
    public void LosAjustesGuardadosComoJsonSeLeen()
    {
        // Asi llegan desde la configuracion guardada del modulo.
        using var doc = JsonDocument.Parse("""{"stability":0.4,"speed":"1.2"}""");
        var config = doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => (object)p.Value.Clone());

        var settings = Parse(ElevenLabsSpeech.BuildRequestJson("eleven_v4", "Hola", config)).GetProperty("voice_settings");

        Assert.Equal(0.4, settings.GetProperty("stability").GetDouble());
        Assert.Equal(1.2, settings.GetProperty("speed").GetDouble());
    }

    [Fact]
    public void ElCosteEsPorCaracter()
    {
        // $80 por 1M de caracteres: 1.000 caracteres son 8 centimos.
        Assert.Equal(0.08m, ElevenLabsSpeech.EstimateCost("eleven_v4", 1_000));
        Assert.Equal(0.04m, ElevenLabsSpeech.EstimateCost("eleven_flash_v2_5", 1_000));
        Assert.Equal(0m, ElevenLabsSpeech.EstimateCost("eleven_v4", 0));
    }

    [Fact]
    public void ElLimiteDeCaracteresDependeDelModelo()
    {
        Assert.Equal(10_000, ElevenLabsSpeech.MaxInputChars("eleven_v4"));
        Assert.Equal(5_000, ElevenLabsSpeech.MaxInputChars("eleven_v3"));
        Assert.Equal(40_000, ElevenLabsSpeech.MaxInputChars("eleven_flash_v2_5"));
        Assert.Equal(5_000, ElevenLabsSpeech.MaxInputChars("modelo-desconocido"));
    }

    [Fact]
    public void LasVocesSeListanConUnaEtiquetaLegible()
    {
        const string json = """
            {
              "voices": [
                { "voice_id": "v1", "name": "Lucia", "category": "premade",
                  "labels": { "gender": "female", "accent": "castilian", "language": "es" } },
                { "voice_id": "v2", "name": "Mi voz", "category": "cloned", "labels": {} },
                { "voice_id": "", "name": "Sin id" }
              ],
              "has_more": true,
              "next_page_token": "tok"
            }
            """;

        var voices = ElevenLabsSpeech.ParseVoices(json, out var next);

        Assert.Equal("tok", next);
        Assert.Equal(2, voices.Count);
        Assert.Equal(("v1", "Lucia · female, castilian, es"), voices[0]);
        Assert.Equal(("v2", "Mi voz · clonada"), voices[1]);
    }

    [Fact]
    public void LaDemostracionMandaLosAjustesDelFormulario()
    {
        var config = SpeechPreview.Configuration("abc123", 1.1, null, stability: 0.3, similarityBoost: 0.9, style: 0.1);

        var json = Parse(ElevenLabsSpeech.BuildRequestJson("eleven_v4", SpeechPreview.Text, config));

        Assert.Contains("/abc123?", ElevenLabsSpeech.BuildRequestUrl(config));
        Assert.Equal(0.3, json.GetProperty("voice_settings").GetProperty("stability").GetDouble());
        Assert.Equal(0.9, json.GetProperty("voice_settings").GetProperty("similarity_boost").GetDouble());
    }

    private static Dictionary<string, object> Config(params (string Key, object Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
