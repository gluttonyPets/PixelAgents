using Server.Services.Ai;
using Xunit;

namespace Server.Tests.Video;

/// <summary>
/// Argumentos con los que se llama a ffmpeg para unir los clips.
///
/// Se prueban aqui y no ejecutando ffmpeg porque el fallo tipico del montaje no
/// es que la herramienta se rompa, sino que el grafo de filtros no cuadre: una
/// etiqueta de mas, un `n` que no coincide con el numero de entradas o un stream
/// de audio declarado que no existe. ffmpeg contesta a eso con un error de
/// sintaxis del filtro que no dice cual de las tres cosas ha pasado.
/// </summary>
public class VideoAssemblerTests
{
    private static string Filter(List<string> args) => args[args.IndexOf("-filter_complex") + 1];

    [Fact]
    public void CadaClipEsUnaEntradaYElConcatDeclaraCuantasSon()
    {
        var args = VideoAssembler.BuildFfmpegArgs(
            ["/tmp/clip_1.mp4", "/tmp/clip_2.mp4", "/tmp/clip_3.mp4"],
            "/tmp/final.mp4", 1280, 720, withAudio: false);

        Assert.Equal(3, args.Count(a => a == "-i"));
        Assert.Contains("concat=n=3:v=1:a=0[outv]", Filter(args));
    }

    [Fact]
    public void SinAudio_NoSeMapeaNingunStreamDeSonido()
    {
        // Motion 2.0 no genera audio. Declarar a=1 sobre clips mudos aborta el
        // montaje entero: es el fallo mas facil de introducir al anadir un
        // proveedor que si trae sonido.
        var args = VideoAssembler.BuildFfmpegArgs(
            ["/tmp/a.mp4", "/tmp/b.mp4"], "/tmp/final.mp4", 1280, 720, withAudio: false);

        var filter = Filter(args);

        Assert.Contains(":v=1:a=0", filter);
        Assert.DoesNotContain("[a0]", filter);
        Assert.DoesNotContain("[outa]", filter);
        Assert.DoesNotContain("-c:a", args);
    }

    [Fact]
    public void ConAudio_CadaEntradaAportaSuPistaYSeCodificaEnAac()
    {
        var args = VideoAssembler.BuildFfmpegArgs(
            ["/tmp/a.mp4", "/tmp/b.mp4"], "/tmp/final.mp4", 1920, 1080, withAudio: true);

        var filter = Filter(args);

        Assert.Contains("[0:a]aresample=async=1[a0];", filter);
        Assert.Contains("[1:a]aresample=async=1[a1];", filter);
        Assert.Contains("[v0][a0][v1][a1]concat=n=2:v=1:a=1[outv][outa]", filter);
        Assert.Contains("aac", args);
    }

    [Fact]
    public void CadaEntradaSeEscalaYRellenaAlLienzoComun()
    {
        // Sin normalizar tamano, SAR y fps, el filtro concat rechaza entradas que
        // no coincidan exactamente. Un pipeline puede mezclar 480p y 720p.
        var args = VideoAssembler.BuildFfmpegArgs(
            ["/tmp/a.mp4", "/tmp/b.mp4"], "/tmp/final.mp4", 1280, 720, withAudio: false);

        var filter = Filter(args);

        Assert.Equal(2, filter.Split("scale=1280:720:force_original_aspect_ratio=decrease").Length - 1);
        Assert.Equal(2, filter.Split("pad=1280:720").Length - 1);
        Assert.Equal(2, filter.Split("setsar=1").Length - 1);
    }

    [Fact]
    public void ElFicheroDeSalidaEsElUltimoArgumento()
    {
        var args = VideoAssembler.BuildFfmpegArgs(
            ["/tmp/a.mp4"], "/tmp/final.mp4", 1280, 720, withAudio: false);

        Assert.Equal("/tmp/final.mp4", args[^1]);
        Assert.Contains("-y", args);          // sobrescribe sin preguntar
        Assert.Contains("libx264", args);
        Assert.Contains("+faststart", args);  // reproducible en web sin bajar el fichero entero
    }

    [Fact]
    public async Task SinClips_NoSeLlamaAFfmpegYSeExplicaPorQue()
    {
        var result = await VideoAssembler.ConcatAsync([], "/tmp/final.mp4");

        Assert.False(result.Success);
        Assert.Contains("No hay clips", result.Error);
    }

    [Fact]
    public async Task ConUnClipQueNoExiste_SeDiceCualFalta()
    {
        var result = await VideoAssembler.ConcatAsync(
            ["/tmp/no-existe-este-clip-12345.mp4"], "/tmp/final.mp4");

        Assert.False(result.Success);
        Assert.Contains("no-existe-este-clip-12345.mp4", result.Error);
    }
}
