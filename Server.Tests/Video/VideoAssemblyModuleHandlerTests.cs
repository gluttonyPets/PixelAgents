using Server.Models;
using Server.Services.Ai;
using Server.Services.Ai.Handlers;
using Xunit;

namespace Server.Tests.Video;

/// <summary>
/// Modulo de montaje. Los casos que no necesitan ffmpeg: que un solo clip se
/// entregue tal cual (recodificarlo solo le quitaria calidad), que lo que no es
/// video no llegue al montaje y que la ausencia de clips se explique en vez de
/// terminar en un error de ffmpeg.
/// </summary>
public class VideoAssemblyModuleHandlerTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), $"pixelagents_test_{Guid.NewGuid():N}");

    public VideoAssemblyModuleHandlerTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task UnSoloClip_SeEntregaSinRecodificar()
    {
        var bytes = new byte[] { 4, 5, 6, 7 };
        var ctx = CreateContext([("clip_1.mp4", "video/mp4", bytes)]);

        var result = await new VideoAssemblyModuleHandler().ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Single(result.ProducedFiles);
        Assert.Equal(VideoAssemblyModuleHandler.OutputFileName, result.ProducedFiles[0].FileName);
        Assert.Equal(bytes, result.ProducedFiles[0].Data);
        Assert.Equal("video", result.Output!.Type);
    }

    [Fact]
    public async Task SinClips_SeDiceQuePuertoConectar()
    {
        var ctx = CreateContext([]);

        var result = await new VideoAssemblyModuleHandler().ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains(VideoAssemblyModuleHandler.InputPort, result.Error);
    }

    [Fact]
    public async Task LoQueNoEsVideo_NoLlegaAlMontaje()
    {
        // Al puerto puede llegar tambien la imagen de origen por un fan-in: darsela
        // a ffmpeg la convertiria en un clip fijo dentro del video final.
        var ctx = CreateContext(
        [
            ("portada.png", "image/png", [1, 1, 1]),
            ("clip_1.mp4", "video/mp4", [2, 2, 2, 2]),
        ]);

        var result = await new VideoAssemblyModuleHandler().ExecuteAsync(ctx);

        // Solo queda un video, asi que se entrega tal cual y no se llama a ffmpeg.
        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Equal(new byte[] { 2, 2, 2, 2 }, result.ProducedFiles[0].Data);
        Assert.Equal(1, (int)result.Output!.Metadata["clips"]);
    }

    private ModuleExecutionContext CreateContext(
        List<(string Name, string ContentType, byte[] Data)> files)
    {
        var aiModule = new AiModule
        {
            Id = Guid.NewGuid(),
            Name = "Montaje de video",
            ModuleType = "VideoAssembly",
            ProviderType = "System",
            ModelName = "video-assembly",
        };
        var projectModule = new ProjectModule { Id = Guid.NewGuid(), IsActive = true, AiModule = aiModule };

        var inputs = new Dictionary<string, List<PortData>>();

        if (files.Count > 0)
        {
            var outputFiles = new List<OutputFile>();
            foreach (var (name, contentType, data) in files)
            {
                File.WriteAllBytes(Path.Combine(_workspace, name), data);
                outputFiles.Add(new OutputFile
                {
                    FileName = name,
                    ContentType = contentType,
                    FileSize = data.Length,
                });
            }

            inputs[VideoAssemblyModuleHandler.InputPort] =
                [new PortData { DataType = "video", Files = outputFiles }];
        }

        return new ModuleExecutionContext
        {
            Node = new ModuleNode(projectModule),
            Graph = new ExecutionGraph(),
            Execution = new ProjectExecution { Id = Guid.NewGuid() },
            Project = new Project { Id = Guid.NewGuid(), Name = "test" },
            TenantDbName = "tenant_test",
            WorkspacePath = _workspace,
            MediaRoot = _workspace,
            Config = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
            InputsByPort = inputs,
        };
    }
}
