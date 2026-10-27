using Server.Services.Ai;
using Xunit;
using Word = Server.Services.Ai.ElevenLabsTimings.Word;

namespace Server.Tests.Subtitulos;

/// <summary>
/// Subtitulos palabra a palabra. ElevenLabs solo da los tiempos de cada palabra;
/// lo que se ve (grupos, resaltado, posicion) sale de aqui y lo pinta ffmpeg.
/// </summary>
public class SubtitulosTests
{
    // ── Tiempos de ElevenLabs ──

    [Fact]
    public void LaAlineacionDevuelvePalabrasConSusTiempos()
    {
        const string json = """
            {"characters":[],"words":[
              {"text":"Tu","start":0.12,"end":0.3,"loss":0.1},
              {"text":"perro","start":0.3,"end":0.71,"loss":0.2},
              {"text":" ","start":0.71,"end":0.72,"loss":0}
            ],"loss":0.15}
            """;

        var words = ElevenLabsTimings.ParseWords(json);

        Assert.Equal([new Word("Tu", 0.12, 0.3), new Word("perro", 0.3, 0.71)], words);
    }

    [Fact]
    public void DeLaTranscripcionSoloCuentanLasPalabras()
    {
        // Scribe mezcla las palabras con los espacios y con eventos (risas, musica).
        const string json = """
            {"text":"Hola perro","words":[
              {"text":"Hola","start":0.0,"end":0.4,"type":"word"},
              {"text":" ","start":0.4,"end":0.45,"type":"spacing"},
              {"text":"(risas)","start":0.45,"end":0.9,"type":"audio_event"},
              {"text":"perro","start":0.9,"end":1.3,"type":"word"},
              {"text":"sin tiempos","type":"word"}
            ]}
            """;

        var words = ElevenLabsTimings.ParseWords(json);

        Assert.Equal(["Hola", "perro"], words.Select(w => w.Text));
    }

    [Fact]
    public void LaTranscripcionPideTiemposPorPalabraYElIdioma()
    {
        using var form = ElevenLabsTimings.BuildTranscriptionForm([1, 2, 3], "audio.mp3", "scribe_v2", "es");
        var parts = form.ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'), p => p);

        Assert.Equal("scribe_v2", parts["model_id"].ReadAsStringAsync().Result);
        Assert.Equal("word", parts["timestamps_granularity"].ReadAsStringAsync().Result);
        Assert.Equal("es", parts["language_code"].ReadAsStringAsync().Result);
        Assert.Equal("false", parts["tag_audio_events"].ReadAsStringAsync().Result);
        Assert.True(parts.ContainsKey("file"));
    }

    [Fact]
    public void LaAlineacionMandaElAudioYElTexto()
    {
        using var form = ElevenLabsTimings.BuildAlignmentForm([1, 2, 3], "audio.mp3", "Tu perro te espera.");
        var parts = form.ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'), p => p);

        Assert.Equal("Tu perro te espera.", parts["text"].ReadAsStringAsync().Result);
        Assert.True(parts.ContainsKey("file"));
    }

    // ── Grupos ──

    private static List<Word> Words(params string[] texts)
    {
        var t = 0d;
        return texts.Select(x => { var w = new Word(x, t, t + 0.3); t += 0.35; return w; }).ToList();
    }

    private static List<string> Groups(List<Word> words, int max) =>
        SubtitleBuilder.Group(words, max).Select(g => string.Join(' ', g.Select(w => w.Text))).ToList();

    [Fact]
    public void LasPalabrasSeAgrupanDeTresEnTres()
    {
        Assert.Equal(["Tu perro te", "espera en la", "puerta"],
            Groups(Words("Tu", "perro", "te", "espera", "en", "la", "puerta"), 3));
    }

    [Fact]
    public void UnGrupoNoJuntaDosFrases()
    {
        Assert.Equal(["¿Lo sabias?", "No es venganza.", "Es angustia."],
            Groups(Words("¿Lo", "sabias?", "No", "es", "venganza.", "Es", "angustia."), 3));
    }

    [Fact]
    public void LaComaCortaSoloSiElGrupoYaTieneDosPalabras()
    {
        // "Si," solo seria un grupo de una palabra; "se tumba," ya puede cerrar.
        Assert.Equal(["Si, se tumba,", "y descansa"],
            Groups(Words("Si,", "se", "tumba,", "y", "descansa"), 4));
    }

    [Fact]
    public void UnSilencioLargoCortaElGrupo()
    {
        var words = new List<Word> { new("Hola", 0, 0.3), new("perro", 3.0, 3.4) };

        Assert.Equal(["Hola", "perro"], Groups(words, 3));
    }

    [Fact]
    public void UnGrupoQueNoCabriaEnDosLineasSeCorta()
    {
        Assert.Equal(["Comportamiento", "extraordinariamente"],
            Groups(Words("Comportamiento", "extraordinariamente"), 3));
    }

    // ── ASS y SRT ──

    [Fact]
    public void CadaPalabraTieneSuMomentoResaltada()
    {
        var groups = SubtitleBuilder.Group(Words("Tu", "perro", "te"), 3);
        var ass = SubtitleBuilder.BuildAss(groups, new SubtitleBuilder.Style(), 1080, 1920);
        var events = ass.Split('\n').Where(l => l.StartsWith("Dialogue:")).ToList();

        // Una linea por palabra: el mismo grupo con otra palabra en color.
        Assert.Equal(3, events.Count);
        Assert.Contains(@"{\1c&H00E6FF&", events[1]);
        Assert.Contains(@"PERRO{\r}", events[1]);
        Assert.StartsWith("Dialogue: 0,0:00:00.35,0:00:00.70,", events[1]);
        // Centro horizontal y al 65 % del alto, con la fuente incluida y tamano relativo al video.
        Assert.Contains(@"{\pos(540,1248)}", events[0]);
        Assert.Contains("Style: TikTok,Poppins ExtraBold,125,", ass);
        Assert.Contains("PlayResY: 1920", ass);
    }

    [Fact]
    public void ElGrupoSeQuedaUnPocoTrasSuUltimaPalabra_SinPisarAlSiguiente()
    {
        var words = new List<Word> { new("Hola.", 0, 0.5), new("Adios", 0.6, 1.0) };
        var groups = SubtitleBuilder.Group(words, 3);
        var events = SubtitleBuilder.BuildAss(groups, new SubtitleBuilder.Style(), 720, 1280)
            .Split('\n').Where(l => l.StartsWith("Dialogue:")).ToList();

        // El primero acaba cuando empieza el segundo (0.6), no a los 0.5 + 0.3.
        Assert.StartsWith("Dialogue: 0,0:00:00.00,0:00:00.60,", events[0]);
        Assert.StartsWith("Dialogue: 0,0:00:00.60,0:00:01.30,", events[1]);
    }

    [Fact]
    public void LasLlavesDelTextoNoAbrenEtiquetasDeAss()
    {
        var groups = SubtitleBuilder.Group([new Word(@"{\b1}hola", 0, 0.5)], 3);
        var ass = SubtitleBuilder.BuildAss(groups, new SubtitleBuilder.Style(Uppercase: false), 720, 1280);

        Assert.Contains("(/b1)hola", ass);
        Assert.DoesNotContain(@"{\b1}", ass);
    }

    [Fact]
    public void ElSrtLlevaUnGrupoPorSubtitulo()
    {
        var groups = SubtitleBuilder.Group(Words("Tu", "perro", "te", "espera."), 3);

        Assert.Equal(
            "1\r\n00:00:00,000 --> 00:00:01,050\r\nTu perro te\r\n\r\n2\r\n00:00:01,050 --> 00:00:01,650\r\nespera.\r\n\r\n",
            SubtitleBuilder.BuildSrt(groups).Replace("\r\n", "\n").Replace("\n", "\r\n"));
    }

    [Theory]
    [InlineData("#FFE600", "&H00E6FF&")]
    [InlineData("#39ff14", "&H14FF39&")]
    public void ElColorPasaAlOrdenDeAss(string hex, string ass)
    {
        Assert.Equal(ass, SubtitleBuilder.AssColor(hex));
    }

    [Fact]
    public void LosTiemposSeEscribenEnElFormatoDeCadaFichero()
    {
        Assert.Equal("1:02:03.46", SubtitleBuilder.AssTime(3723.456));
        Assert.Equal("01:02:03,456", SubtitleBuilder.SrtTime(3723.456));
    }

    // ── Configuracion ──

    [Fact]
    public void LaConfiguracionSeAcotaYSinElla_EstiloTikTok()
    {
        var porDefecto = SubtitleBuilder.Style.FromConfig(new Dictionary<string, object>());
        var fuera = SubtitleBuilder.Style.FromConfig(new Dictionary<string, object>
        {
            [SubtitleBuilder.WordsKey] = 9L,
            [SubtitleBuilder.ColorKey] = "amarillo",
            [SubtitleBuilder.SizeKey] = 40.0,
            [SubtitleBuilder.PositionKey] = 1L,
            [SubtitleBuilder.UppercaseKey] = false,
        });

        Assert.Equal(new SubtitleBuilder.Style(3, "#FFE600", 6.5, 65, true), porDefecto);
        Assert.Equal(new SubtitleBuilder.Style(5, "#FFE600", 10, 15, false), fuera);
    }

    [Fact]
    public void ElEstiloEsDelModulo_NoDeUnaCopiaEnElNodo()
    {
        Assert.True(ModuleConfigKeys.IsModuleOwned(SubtitleBuilder.ColorKey));
        Assert.True(ModuleConfigKeys.IsModuleOwned(SubtitleBuilder.WordsKey));
    }

    [Fact]
    public void ElModeloDeSubtitulosEstaEnElCatalogoYTieneTarifa()
    {
        var model = Assert.Single(ModelCatalog.GetByModuleType("Subtitles"));

        Assert.Equal("scribe_v2", model.Id);
        Assert.Equal("ElevenLabs", model.Provider);
        // 0,22 $ la hora de audio.
        Assert.Equal(0.0037m, PricingCatalog.GetAuxiliaryRate("scribe_v2")!.Amount);
    }
}
