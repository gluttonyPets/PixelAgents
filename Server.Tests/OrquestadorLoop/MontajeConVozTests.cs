using Server.Models;
using Server.Services.Ai;
using Server.Services.Ai.Handlers;
using Xunit;

namespace Server.Tests.OrquestadorLoop;

/// <summary>
/// Montaje con voz en off: la voz es la pista y manda en la duracion. Es el
/// cierre del orquestador loop: N clips de 5 s casi nunca suman lo mismo que la
/// voz, y el montaje recorta o congela el final para que cuadren.
/// </summary>
public class MontajeConVozTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), $"pixelagents_voz_{Guid.NewGuid():N}");

    public MontajeConVozTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static string Filter(List<string> args) => args[args.IndexOf("-filter_complex") + 1];

    [Fact]
    public void LaVozEsLaUnicaPistaYMarcaLaDuracion()
    {
        var args = VideoAssembler.BuildFfmpegArgsWithVoice(
            ["/tmp/clip_1.mp4", "/tmp/clip_2.mp4"], ["/tmp/voz.mp3"], "/tmp/final.mp4", 720, 1280, 39.04);

        var filter = Filter(args);

        Assert.Equal(3, args.Count(a => a == "-i"));
        // Los clips se pegan sin su audio y el final se congela tanto como haga falta...
        Assert.Contains("concat=n=2:v=1:a=0[cv]", filter);
        Assert.Contains("[cv]tpad=stop_mode=clone:stop_duration=39.04[outv]", filter);
        // ...la pista es la voz (entrada 2)...
        Assert.Contains("[2:a]aresample=async=1[va0]", filter);
        Assert.Contains("[va0]anull[outa]", filter);
        // ...y el video se corta donde acaba la voz.
        Assert.Equal("39.04", args[args.IndexOf("-t") + 1]);
        Assert.Contains("[outa]", args);
    }

    [Fact]
    public void VariasTomasDeVoz_SePeganEnOrden()
    {
        var args = VideoAssembler.BuildFfmpegArgsWithVoice(
            ["/tmp/clip_1.mp4"], ["/tmp/voz_1.mp3", "/tmp/voz_2.mp3", "/tmp/voz_3.mp3"],
            "/tmp/final.mp4", 720, 1280, 14);

        Assert.Contains("[va0][va1][va2]concat=n=3:v=0:a=1[outa]", Filter(args));
        Assert.Contains("[3:a]aresample=async=1[va2]", Filter(args));
    }

    [Fact]
    public void SinPoderMedirLaVoz_SeCortaPorLaPistaMasCorta()
    {
        var args = VideoAssembler.BuildFfmpegArgsWithVoice(
            ["/tmp/clip_1.mp4"], ["/tmp/voz.mp3"], "/tmp/final.mp4", 720, 1280, null);

        Assert.Contains("-shortest", args);
        Assert.DoesNotContain("-t", args);
        Assert.DoesNotContain("tpad", Filter(args));
    }

    [Theory]
    [InlineData(39, 15, "se congela el ultimo fotograma 24 s")]
    [InlineData(38, 40, "se recortan 2 s del final")]
    [InlineData(30, 30, "duran lo mismo")]
    public void ElLogDiceQueHuboQueHacer(double voice, double clips, string expected)
    {
        Assert.Contains(expected, VideoAssembler.DescribeFit(voice, clips));
    }

    [Fact]
    public async Task ConFfmpeg_ElVideoDuraLoQueLaVoz()
    {
        if (!MediaFixtures.HasFfmpeg) return;

        // 2 clips de 1 s y una voz de 3.5 s: sin voz saldrian 2 s.
        var clips = new[]
        {
            MediaFixtures.Clip(_workspace, "clip_1.mp4", 1),
            MediaFixtures.Clip(_workspace, "clip_2.mp4", 1),
        };
        var voice = MediaFixtures.Silence(_workspace, "voz.mp3", 3.5);
        var output = Path.Combine(_workspace, "final.mp4");

        var result = await VideoAssembler.ConcatAsync(clips, [voice], output);

        Assert.True(result.Success, result.Error);
        Assert.InRange(MediaFixtures.Duration(output), 3.3, 3.7);
        Assert.True(MediaFixtures.HasAudio(output));
        Assert.Contains("se congela el ultimo fotograma", result.Note);
    }

    [Fact]
    public async Task ConFfmpeg_ClipsMasLargosQueLaVoz_SeRecortan()
    {
        if (!MediaFixtures.HasFfmpeg) return;

        var clips = new[]
        {
            MediaFixtures.Clip(_workspace, "clip_1.mp4", 2),
            MediaFixtures.Clip(_workspace, "clip_2.mp4", 2),
        };
        var voice = MediaFixtures.Silence(_workspace, "voz.mp3", 2.5);
        var output = Path.Combine(_workspace, "final.mp4");

        var result = await VideoAssembler.ConcatAsync(clips, [voice], output);

        Assert.True(result.Success, result.Error);
        Assert.InRange(MediaFixtures.Duration(output), 2.3, 2.7);
    }

    [Fact]
    public async Task ConFfmpeg_LaDuracionSeMideBien()
    {
        if (!MediaFixtures.HasFfmpeg) return;

        var seconds = await VideoAssembler.ProbeDurationAsync(MediaFixtures.Silence(_workspace, "voz.mp3", 4));

        Assert.NotNull(seconds);
        Assert.InRange(seconds!.Value, 3.9, 4.2);
    }

    [Fact]
    public async Task UnFicheroQueNoEsMedia_NoTieneDuracion()
    {
        var path = Path.Combine(_workspace, "nada.mp3");
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.Null(await VideoAssembler.ProbeDurationAsync(path));
    }

    [Fact]
    public async Task Modulo_UnSoloClipConVoz_SeMontaEnVezDeEntregarseTalCual()
    {
        if (!MediaFixtures.HasFfmpeg) return;

        var clip = MediaFixtures.Clip(_workspace, "clip_1.mp4", 1);
        var voice = MediaFixtures.Silence(_workspace, "voz.mp3", 2);
        var ctx = CreateContext(
            [new OutputFile { FileName = clip, ContentType = "video/mp4" }],
            [new OutputFile { FileName = voice, ContentType = "audio/mpeg" }]);

        var result = await new VideoAssemblyModuleHandler().ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Equal(1, (int)result.Output!.Metadata["voices"]);
        var output = Path.Combine(_workspace, "salida.mp4");
        File.WriteAllBytes(output, result.ProducedFiles[0].Data);
        Assert.InRange(MediaFixtures.Duration(output), 1.8, 2.2);
        Assert.True(MediaFixtures.HasAudio(output));
    }

    private ModuleExecutionContext CreateContext(List<OutputFile> clips, List<OutputFile> voices)
    {
        var aiModule = new AiModule
        {
            Id = Guid.NewGuid(),
            Name = "Montaje de video",
            ModuleType = "VideoAssembly",
            ProviderType = "System",
            ModelName = "video-assembly",
        };

        return new ModuleExecutionContext
        {
            Node = new ModuleNode(new ProjectModule { Id = Guid.NewGuid(), IsActive = true, AiModule = aiModule }),
            Graph = new ExecutionGraph(),
            Execution = new ProjectExecution { Id = Guid.NewGuid() },
            Project = new Project { Id = Guid.NewGuid(), Name = "test" },
            TenantDbName = "tenant_test",
            WorkspacePath = _workspace,
            MediaRoot = _workspace,
            Config = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
            InputsByPort = new Dictionary<string, List<PortData>>
            {
                [VideoAssemblyModuleHandler.InputPort] = [new PortData { DataType = "video", Files = clips }],
                [VideoAssemblyModuleHandler.VoicePort] = [new PortData { DataType = "audio", Files = voices }],
            },
        };
    }
}
