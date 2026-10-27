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
        Assert.Equal("v1", voices[0].Id);
        Assert.Equal("Lucia · female, castilian, es", voices[0].Label);
        Assert.Null(voices[0].OwnerId);
        Assert.Equal("Mi voz · clonada", voices[1].Label);
    }

    [Fact]
    public void LasVocesDeLaBibliotecaLlevanSuDueno()
    {
        const string json = """
            {
              "voices": [
                { "voice_id": "lib1", "public_owner_id": "own1", "name": "Carmen",
                  "gender": "female", "age": "middle_aged", "accent": "peninsular",
                  "descriptive": "calm", "language": "es",
                  "preview_url": "https://example.com/carmen.mp3" },
                { "voice_id": "lib2", "name": "Sin dueno" }
              ],
              "has_more": false
            }
            """;

        var voices = ElevenLabsSpeech.ParseSharedVoices(json);

        var carmen = Assert.Single(voices);
        Assert.Equal("lib1", carmen.Id);
        Assert.Equal("own1", carmen.OwnerId);
        Assert.Equal("Carmen · female, middle_aged, peninsular, calm", carmen.Label);
        Assert.Equal("https://example.com/carmen.mp3", carmen.PreviewUrl);
    }

    [Fact]
    public void LaBibliotecaSeBuscaPorIdiomaYLasMasUsadasPrimero()
    {
        var url = ElevenLabsSpeech.BuildSharedVoicesUrl("es");

        Assert.StartsWith("https://api.elevenlabs.io/v1/shared-voices?", url);
        Assert.Contains("language=es", url);
        Assert.Contains("sort=usage_character_count_1y", url);
    }

    [Fact]
    public void SeDetectaLaVozQueNoEstaEnLaCuenta()
    {
        const string body = """{"detail":{"status":"voice_not_found","message":"A voice with voice_id x was not found."}}""";

        Assert.True(ElevenLabsSpeech.IsVoiceNotFound(400, body));
        Assert.True(ElevenLabsSpeech.IsVoiceNotFound(404, body));
        Assert.False(ElevenLabsSpeech.IsVoiceNotFound(401, body));
        Assert.False(ElevenLabsSpeech.IsVoiceNotFound(400, """{"detail":{"status":"invalid_api_key"}}"""));
    }

    [Fact]
    public void LaVozDeBibliotecaGuardadaLlevaSuDueno()
    {
        var config = Config(("voice", "lib1"), (ElevenLabsSpeech.VoiceOwnerKey, "own1"), ("voiceName", "Carmen"));

        Assert.Equal("own1", ElevenLabsSpeech.VoiceOwner(config));
        Assert.Equal("Carmen", ElevenLabsSpeech.VoiceName(config));
        Assert.Equal("https://api.elevenlabs.io/v1/voices/add/own1/lib1",
            ElevenLabsSpeech.BuildAddSharedVoiceUrl("own1", "lib1"));
    }

    [Fact]
    public void LaBibliotecaSeFiltraPorAcentoYUso()
    {
        var url = ElevenLabsSpeech.BuildSharedVoicesUrl("es", "latin american", "narrative_story", page: 2);

        Assert.Contains("accent=latin%20american", url);
        Assert.Contains("use_cases=narrative_story", url);
        Assert.Contains("page=2", url);
        Assert.DoesNotContain("accent=", ElevenLabsSpeech.BuildSharedVoicesUrl("es", " "));
    }

    [Fact]
    public void LosAcentosSeSacanDeLasVocesConSuFrecuencia()
    {
        var voices = new[]
        {
            new ElevenLabsSpeech.Voice("1", "A", null, "o", Accent: "argentine"),
            new ElevenLabsSpeech.Voice("2", "B", null, "o", Accent: "Peninsular"),
            new ElevenLabsSpeech.Voice("3", "C", null, "o", Accent: "peninsular"),
            new ElevenLabsSpeech.Voice("4", "D", null, "o"),
        };

        var accents = ElevenLabsSpeech.CountFacet(voices, v => v.Accent);

        Assert.Equal([("peninsular", 2), ("argentine", 1)], accents);
    }

    [Fact]
    public void LasVocesDeLaBibliotecaTraenAcentoYUso()
    {
        const string json = """
            {"voices":[{"voice_id":"v","public_owner_id":"o","name":"Beto",
              "accent":"argentine","use_case":"narrative_story"}]}
            """;

        var voice = Assert.Single(ElevenLabsSpeech.ParseSharedVoices(json));

        Assert.Equal("argentine", voice.Accent);
        Assert.Equal("narrative_story", voice.UseCase);
    }

    [Fact]
    public void CadaBloqueLlevaUnaPausa_UnaLineaSimpleNo()
    {
        var json = Parse(ElevenLabsSpeech.BuildRequestJson("eleven_multilingual_v2",
            "Tu perro te espera.\n¿Sabes por que?\n\nNo es venganza.", Config()));

        Assert.Equal("Tu perro te espera.\n¿Sabes por que? <break time=\"0.5s\" />\n\nNo es venganza.",
            json.GetProperty("text").GetString());
    }

    [Fact]
    public void UnTextoConSusPropiasPausasNoSeToca()
    {
        const string text = "Hola. <break time=\"1s\" />\n\nAdios.";

        Assert.Equal(text, ElevenLabsSpeech.WithPauses("eleven_multilingual_v2", text));
    }

    [Theory]
    [InlineData("eleven_v4")]
    [InlineData("eleven_v4_turbo")]
    [InlineData("eleven_v3")]
    public void V3YV4NoEntiendenBreak_LaPausaEsElParrafo(string model)
    {
        // Leerian la etiqueta o la ignorarian: ahi pausan con la puntuacion y el parrafo.
        const string text = "Tu perro te espera.\n\nNo es venganza.";

        Assert.Equal(text, ElevenLabsSpeech.WithPauses(model, text));
    }

    [Fact]
    public void SinParrafosNoSeAnadenPausas_YDemasiadasSeLimitan()
    {
        Assert.Equal("Una sola frase.", ElevenLabsSpeech.WithPauses("eleven_flash_v2_5", "Una sola frase."));

        var muchos = string.Join("\n\n", Enumerable.Range(1, 12).Select(i => $"Frase {i}."));
        var text = ElevenLabsSpeech.WithPauses("eleven_flash_v2_5", muchos);

        // Muchas pausas en una locucion la vuelven inestable (aviso de ElevenLabs).
        Assert.Equal(ElevenLabsSpeech.MaxPauses, text.Split(ElevenLabsSpeech.ParagraphPause).Length - 1);
        Assert.EndsWith("Frase 12.", text);
    }

    [Fact]
    public void LaMuestraEsLaDelIdiomaBuscado_ConElModeloConQueSeGrabo()
    {
        // La muestra general puede estar en ingles; la que se oye al elegir voz en
        // espanol tiene que ser la de espanol, y del acento de la voz si hay varias.
        const string json = """
            {"voices":[{"voice_id":"v","public_owner_id":"o","name":"Carla","accent":"peninsular",
              "preview_url":"https://storage.googleapis.com/eleven-public-prod/en.mp3",
              "verified_languages":[
                {"language":"en","model_id":"eleven_multilingual_v2","preview_url":"https://storage.googleapis.com/eleven-public-prod/en.mp3"},
                {"language":"es","model_id":"eleven_flash_v2_5","accent":"latin american","preview_url":"https://storage.googleapis.com/eleven-public-prod/es-latam.mp3"},
                {"language":"es","model_id":"eleven_multilingual_v2","accent":"peninsular","preview_url":"https://storage.googleapis.com/eleven-public-prod/es.mp3"}
              ]}]}
            """;

        var es = Assert.Single(ElevenLabsSpeech.ParseSharedVoices(json, "es"));
        var sinIdioma = Assert.Single(ElevenLabsSpeech.ParseSharedVoices(json));
        var fr = Assert.Single(ElevenLabsSpeech.ParseSharedVoices(json, "fr"));

        Assert.Equal("https://storage.googleapis.com/eleven-public-prod/es.mp3", es.PreviewUrl);
        Assert.Equal("eleven_multilingual_v2", es.PreviewModel);
        Assert.Equal("https://storage.googleapis.com/eleven-public-prod/en.mp3", sinIdioma.PreviewUrl);
        Assert.Null(sinIdioma.PreviewModel);
        Assert.Equal("https://storage.googleapis.com/eleven-public-prod/en.mp3", fr.PreviewUrl);
        Assert.Null(fr.PreviewModel);
    }

    [Fact]
    public void LaVozDeBibliotecaYaAnadidaSeEncuentraPorSuIdOriginal()
    {
        // La copia en la cuenta puede tener otro id: si no se buscara por el
        // original, cada locucion volveria a anadirla y ocuparia otro hueco.
        const string json = """
            {"voices":[
              {"voice_id":"copia1","name":"Carmen","sharing":{"original_voice_id":"lib1"}},
              {"voice_id":"lib2","name":"Beto","sharing":{"original_voice_id":"lib2"}},
              {"voice_id":"propia","name":"Mi voz","sharing":null}
            ]}
            """;

        var account = ElevenLabsSpeech.ParseVoices(json, out _);

        Assert.Equal("lib1", account[0].OriginalVoiceId);
        Assert.Null(account[1].OriginalVoiceId);
        Assert.Null(account[2].OriginalVoiceId);
        Assert.Equal("copia1", ElevenLabsSpeech.FindAddedVoice(account, "lib1"));
        Assert.Equal("lib2", ElevenLabsSpeech.FindAddedVoice(account, "lib2"));
        Assert.Null(ElevenLabsSpeech.FindAddedVoice(account, "lib3"));
    }

    [Theory]
    [InlineData("https://storage.googleapis.com/eleven-public-prod/premade/voices/abc/sample.mp3")]
    [InlineData(" https://storage.googleapis.com/eleven-public-cdn/x/y.mp3 ")]
    public void LaMuestraDeElevenLabsSePuedeReenviar(string url)
    {
        Assert.True(ElevenLabsSpeech.TryParseSampleUrl(url, out var sample));
        Assert.Equal("storage.googleapis.com", sample.Host);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no es una url")]
    [InlineData("http://storage.googleapis.com/eleven-public-prod/a.mp3")]
    [InlineData("https://evil.com/eleven-public-prod/a.mp3")]
    [InlineData("https://storage.googleapis.com.evil.com/eleven-public-prod/a.mp3")]
    [InlineData("https://storage.googleapis.com@evil.com/eleven-public-prod/a.mp3")]
    [InlineData("https://user:pass@storage.googleapis.com/eleven-public-prod/a.mp3")]
    [InlineData("https://storage.googleapis.com:8443/eleven-public-prod/a.mp3")]
    [InlineData("https://storage.googleapis.com/otro-bucket/a.mp3")]
    public void ElEndpointDeMuestrasNoEsUnProxyAbierto(string? url)
    {
        Assert.False(ElevenLabsSpeech.TryParseSampleUrl(url, out _));
    }

    [Theory]
    [InlineData("https://storage.googleapis.com/eleven-x/a.mp3", "audio/mpeg")]
    [InlineData("https://storage.googleapis.com/eleven-x/a", "audio/mpeg")]
    [InlineData("https://storage.googleapis.com/eleven-x/a.WAV", "audio/wav")]
    [InlineData("https://storage.googleapis.com/eleven-x/a.ogg", "audio/ogg")]
    public void LaMuestraSeSirveComoAudio(string url, string contentType)
    {
        // Google Storage la entrega como text/plain, que Firefox y Safari no reproducen.
        Assert.Equal(contentType, ElevenLabsSpeech.SampleContentType(new Uri(url)));
    }

    private static Dictionary<string, object> Config(params (string Key, object Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
