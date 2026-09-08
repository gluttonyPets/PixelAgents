using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Montaje: pega los clips que le llegan en un unico video.
///
/// Es un modulo de sistema, sin proveedor ni modelo: todo el trabajo lo hace
/// ffmpeg en local, asi que no consume API key ni tiene coste por llamada.
///
/// Va aparte del modulo de video a proposito. Generar y montar son dos cosas que
/// fallan por motivos distintos —una por la API del proveedor, la otra por el
/// fichero— y separarlas permite volver a montar sin volver a pagar la
/// generacion, ademas de dejar montar clips que vengan de sitios distintos.
///
/// El ORDEN es el de llegada por el puerto de entrada, que en el grafo es el
/// orden de las conexiones. Un modulo de video que produce cinco clips los
/// entrega ya ordenados (clip_1..clip_5).
/// </summary>
public class VideoAssemblyModuleHandler : IModuleHandler
{
    public const string InputPort = "input_videos";
    public const string OutputFileName = "video_final.mp4";

    public string ModuleType => "VideoAssembly";

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var clips = CollectClips(ctx);

        if (clips.Count == 0)
            return ModuleResult.Failed(
                $"Sin clips que montar. Conecta un modulo de video al puerto '{InputPort}'.");

        var paths = clips.Select(ctx.ResolveOutputFilePath).ToList();

        // Un solo clip no necesita recodificarse: el montaje de uno es ese uno.
        // Pasarlo por ffmpeg solo le quitaria calidad.
        if (paths.Count == 1)
        {
            var single = await File.ReadAllBytesAsync(paths[0], ctx.CancellationToken);
            await ctx.LogInfoAsync("Un unico clip: se entrega tal cual, sin recodificar");
            return Deliver(ctx, single, clips.Count);
        }

        await ctx.LogInfoAsync($"Montando {paths.Count} clips en un unico video");

        var tempPath = Path.Combine(Path.GetTempPath(), $"pixelagents_{Guid.NewGuid():N}.mp4");

        try
        {
            var result = await VideoAssembler.ConcatAsync(paths, tempPath, ctx.CancellationToken);
            if (!result.Success)
                return ModuleResult.Failed(result.Error ?? "El montaje del video fallo");

            var bytes = await File.ReadAllBytesAsync(tempPath, ctx.CancellationToken);
            await ctx.LogInfoAsync($"Video montado: {bytes.Length / 1024} KB");
            return Deliver(ctx, bytes, clips.Count);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* el temporal se lo lleva el sistema */ }
        }
    }

    private static ModuleResult Deliver(ModuleExecutionContext ctx, byte[] data, int clipCount)
    {
        var produced = new List<ProducedFile>
        {
            new() { Data = data, FileName = OutputFileName, ContentType = "video/mp4" },
        };

        var outputFiles = new List<OutputFile>
        {
            new() { FileName = OutputFileName, ContentType = "video/mp4", FileSize = data.Length },
        };

        var output = OutputSchemaHelper.BuildVideoOutput(outputFiles, "ffmpeg", new Dictionary<string, object>
        {
            ["clips"] = clipCount,
        });

        return ModuleResult.Completed(output, 0m, produced);
    }

    /// <summary>
    /// Ficheros de video que llegan al modulo. Se acepta cualquier puerto porque
    /// el montaje no tiene nada que decidir segun por donde entren, y se filtra
    /// por tipo de contenido: si al puerto llega tambien una imagen o un texto
    /// (fan-in de un modulo que emite varias cosas), ffmpeg no tiene que verlo.
    /// </summary>
    private static List<OutputFile> CollectClips(ModuleExecutionContext ctx)
    {
        var files = ctx.GetInputFiles(InputPort);

        if (files.Count == 0)
            files = ctx.InputsByPort
                .SelectMany(kv => kv.Value)
                .Where(d => d.Files is not null)
                .SelectMany(d => d.Files!)
                .ToList();

        return files
            .Where(f => f.ContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
    }
}
