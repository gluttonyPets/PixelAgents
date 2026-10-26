using System.Text.Json;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.Video;

/// <summary>
/// Lo que se puede configurar de cada modelo de video.
///
/// Cada API de video rechaza con un 400 cualquier combinacion que no este en su
/// lista, y el modulo guarda lo que el usuario eligio con OTRO modelo, o lo que
/// guardaba antes de que hubiera opciones (Motion 2.0 con "RESOLUTION_480"). Por
/// eso todo pasa por <see cref="VideoModelOptions.Resolve"/>, que lo encaja en lo
/// permitido mas cercano en vez de dejar que el error salga a mitad de pipeline.
/// </summary>
public class OpcionesVideoTests
{
    private static VideoModelOptions.Spec Spec(string id) =>
        VideoModelOptions.Find(id) ?? throw new InvalidOperationException($"{id} sin opciones");

    private static VideoModelOptions.Resolved Resolve(
        string id, Dictionary<string, object>? config = null, (int, int)? image = null) =>
        VideoModelOptions.Resolve(Spec(id), config ?? new Dictionary<string, object>(), image);

    // ── Coherencia de la tabla ──

    [Fact]
    public void TodoModeloDeVideoDelCatalogoTieneOpcionesYSabeLlamarloElProvider()
    {
        // Un modelo en el catalogo sin opciones se podria elegir en la pantalla pero
        // fallaria al ejecutar; uno con opciones pero fuera del catalogo, al reves.
        var catalogo = ModelCatalog.GetByModuleType("Video").Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var opciones = VideoModelOptions.Specs.Select(s => s.ModelId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var provider = LeonardoProvider.VideoModelIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Todos los modelos de video de la API v2 de Leonardo.
        Assert.Equal(32, catalogo.Count);
        Assert.True(catalogo.SetEquals(opciones), "catalogo y VideoModelOptions no tienen los mismos modelos");
        Assert.True(catalogo.SetEquals(provider), "catalogo y LeonardoProvider no tienen los mismos modelos");
    }

    [Fact]
    public void LosValoresPorDefectoEstanEntreLosPermitidos()
    {
        foreach (var spec in VideoModelOptions.Specs)
        {
            Assert.Contains(spec.DefaultDuration, spec.Durations);
            Assert.Contains(spec.Resolutions, r => r.Id == spec.DefaultResolution);

            foreach (var r in spec.Resolutions)
            {
                Assert.NotEmpty(r.Sizes);
                Assert.All(r.Durations ?? [], d => Assert.Contains(d, spec.Durations));
            }

            if (spec.Qualities is { Length: > 0 } qualities)
                Assert.Contains(qualities, q => q.Value == spec.DefaultQuality);
        }
    }

    [Fact]
    public void SinConfiguracion_SeUsanLosValoresPorDefectoDelModelo()
    {
        var veo = Resolve("leonardo-veo-3.1");

        Assert.Equal(8, veo.Duration);
        Assert.Equal("720p", veo.Resolution.Id);
        Assert.Equal((1280, 720), (veo.Size.Width, veo.Size.Height));
        Assert.False(veo.Audio);
    }

    // ── Duracion ──

    [Fact]
    public void UnaDuracionFueraDeRango_SeEncajaEnLaMasCercana()
    {
        Assert.Equal(15, Resolve("leonardo-kling-3", new() { ["duration"] = 40L }).Duration);
        Assert.Equal(3, Resolve("leonardo-kling-3", new() { ["duration"] = 1L }).Duration);
        // A igual distancia gana la corta: es la mas barata, y el formulario hace lo mismo.
        Assert.Equal(4, Resolve("leonardo-veo-3.1", new() { ["duration"] = 5L }).Duration);
    }

    [Fact]
    public void LaDuracionLlegaDeCualquierFormaQueDejeElJsonDeConfiguracion()
    {
        Assert.Equal(7, Resolve("leonardo-kling-3", new() { ["duration"] = 7.0 }).Duration);
        Assert.Equal(7, Resolve("leonardo-kling-3", new() { ["duration"] = "7" }).Duration);
        Assert.Equal(7, Resolve("leonardo-kling-3", new() { ["duration"] = JsonDocument.Parse("7").RootElement }).Duration);
    }

    [Fact]
    public void AlgunasResolucionesSoloAdmitenAlgunasDuraciones()
    {
        // Hailuo 2.3 (y su Fast) hace 10 s a 768p pero solo 6 s a 1080p; Veo, 1080p solo a 8 s.
        Assert.Equal(10, Resolve("leonardo-hailuo-2.3-fast", new() { ["resolution"] = "768p", ["duration"] = 10L }).Duration);
        Assert.Equal(6, Resolve("leonardo-hailuo-2.3-fast", new() { ["resolution"] = "1080p", ["duration"] = 10L }).Duration);
        Assert.Equal(6, Resolve("leonardo-hailuo-2.3", new() { ["resolution"] = "1080p", ["duration"] = 10L }).Duration);
        Assert.Equal(8, Resolve("leonardo-veo-3.1", new() { ["resolution"] = "1080p", ["duration"] = 4L }).Duration);
    }

    [Fact]
    public void MotionTieneDuracionFija()
    {
        Assert.Equal(5, Resolve("leonardo-motion-2", new() { ["duration"] = 10L }).Duration);
        Assert.False(Spec("leonardo-motion-2").SendDuration);
    }

    [Fact]
    public void ResolveDurationEsLoQueUsaElOrquestadorLoop()
    {
        Assert.Equal(12, VideoModelOptions.ResolveDuration("leonardo-wan-3", new Dictionary<string, object> { ["duration"] = 12L }));
        Assert.Null(VideoModelOptions.ResolveDuration("gpt-image-2", null));
    }

    // ── Resolucion y formato ──

    [Fact]
    public void LaResolucionGuardadaPorLosModulosAntiguosSigueValiendo()
    {
        // Antes de las opciones por modelo, Motion 2.0 guardaba "RESOLUTION_720".
        Assert.Equal("720p", Resolve("leonardo-motion-2", new() { ["resolution"] = "RESOLUTION_720" }).Resolution.Id);
        Assert.Equal("480p", Resolve("leonardo-motion-2", new() { ["resolution"] = "RESOLUTION_480" }).Resolution.Id);
    }

    [Fact]
    public void UnaResolucionQueElModeloNoTiene_CaeEnLaPorDefecto()
    {
        // 4K es de Veo 3.1, no de Veo 3.1 Lite.
        Assert.Equal("720p", Resolve("leonardo-veo-3.1-lite", new() { ["resolution"] = "4k" }).Resolution.Id);
    }

    [Fact]
    public void EnAutomatico_ElFormatoSigueLaProporcionDeLaImagen()
    {
        var vertical = Resolve("leonardo-kling-3", new() { ["resolution"] = "720p" }, (1080, 1920));
        var cuadrada = Resolve("leonardo-kling-3", new() { ["resolution"] = "720p" }, (1024, 1024));
        var desconocida = Resolve("leonardo-kling-3", new() { ["resolution"] = "720p" }, (0, 0));

        Assert.Equal((720, 1280), (vertical.Size.Width, vertical.Size.Height));
        Assert.Equal((960, 960), (cuadrada.Size.Width, cuadrada.Size.Height));
        Assert.Equal((1280, 720), (desconocida.Size.Width, desconocida.Size.Height));
    }

    [Fact]
    public void UnFormatoElegido_MandaSobreLaImagen()
    {
        var forzado = Resolve("leonardo-kling-3", new() { ["resolution"] = "1080p", ["aspectRatio"] = "16:9" }, (1080, 1920));

        Assert.Equal((1920, 1080), (forzado.Size.Width, forzado.Size.Height));
    }

    [Fact]
    public void UnFormatoQueLaResolucionNoTiene_CaeEnElMasParecido()
    {
        // Veo solo hace 16:9 y 9:16: un 4:5 vertical se queda en 9:16.
        var veo = Resolve("leonardo-veo-3.1", new() { ["aspectRatio"] = "4:5" });

        Assert.Equal((720, 1280), (veo.Size.Width, veo.Size.Height));
    }

    // ── Audio y extras ──

    [Fact]
    public void ElAudioVaApagadoSalvoQueSePida_YSoloDondeExiste()
    {
        Assert.False(Resolve("leonardo-kling-3").Audio);
        Assert.True(Resolve("leonardo-kling-3", new() { ["audio"] = true }).Audio);
        // Hailuo 03 lo genera siempre; Motion no lo genera nunca.
        Assert.True(Resolve("leonardo-hailuo-03", new() { ["audio"] = false }).Audio);
        Assert.Null(Resolve("leonardo-motion-2", new() { ["audio"] = true }).Audio);
    }

    [Fact]
    public void LosExtrasSoloSeMandanAlModeloQueLosAdmite()
    {
        var config = new Dictionary<string, object>
        {
            ["negativePrompt"] = "texto, marcas de agua",
            ["seed"] = 42L,
            ["promptEnhance"] = true,
            ["frameInterpolation"] = false,
        };

        var motion = Resolve("leonardo-motion-2", config);
        Assert.Equal("texto, marcas de agua", motion.NegativePrompt);
        Assert.Equal(42, motion.Seed);
        Assert.True(motion.PromptEnhance);
        Assert.False(motion.FrameInterpolation);

        var kling = Resolve("leonardo-kling-3", config);
        Assert.Null(kling.NegativePrompt);
        Assert.Null(kling.Seed);
        Assert.Null(kling.PromptEnhance);
        Assert.Null(kling.FrameInterpolation);
    }

    [Fact]
    public void UnPromptNegativoLargoSeRecorta_YUnaSemillaFueraDeRangoSeIgnora()
    {
        var motion = Resolve("leonardo-motion-2", new()
        {
            ["negativePrompt"] = new string('x', 1500),
            ["seed"] = -5L,
        });

        Assert.Equal(1000, motion.NegativePrompt!.Length);
        Assert.Null(motion.Seed);
    }

    [Fact]
    public void LaCalidadDeGeneracionSeEncajaEnLasDelModelo()
    {
        Assert.Equal("STANDARD", Resolve("leonardo-hailuo-03", new() { ["videoQuality"] = "standard" }).Quality);
        Assert.Equal("TURBO", Resolve("leonardo-hailuo-03", new() { ["videoQuality"] = "ULTRA" }).Quality);
        Assert.Equal("ACCELERATED", Resolve("leonardo-wan-3").Quality);
        Assert.Null(Resolve("leonardo-veo-3.1").Quality);
    }

    // ── Lo que viaja a la pantalla ──

    [Fact]
    public void LaPantallaRecibeLasOpcionesDeCadaModelo()
    {
        var hailuo = VideoModelOptions.Describe().Single(o => o.ModelId == "leonardo-hailuo-2.3-fast");

        Assert.Equal(new[] { 6, 10 }, hailuo.Durations);
        Assert.Equal("none", hailuo.Audio);
        Assert.Equal(new[] { 6 }, hailuo.Resolutions.Single(r => r.Id == "1080p").Durations);
        Assert.Contains("9:16", hailuo.Resolutions.Single(r => r.Id == "768p").Aspects);
    }
}
