using System.Diagnostics;
using System.Globalization;

namespace Server.Services.Ai;

/// <summary>
/// Une varios clips en un unico MP4 llamando a ffmpeg.
///
/// No usa el demuxer `concat` (el de la lista de ficheros) sino el FILTRO
/// `concat`, que reescala cada entrada a un lienzo comun antes de pegarlas. El
/// demuxer es mas barato pero exige que todos los clips compartan codec,
/// resolucion, fps y SAR exactos; en un pipeline los clips pueden venir de
/// modelos y resoluciones distintas, y en ese caso el demuxer produce un fichero
/// que unos reproductores abren y otros no, sin dar error al generarlo. Pagar la
/// recodificacion es preferible a entregar un video roto en silencio.
///
/// El audio solo se conserva si TODOS los clips lo traen: el filtro concat exige
/// el mismo numero de streams en todas las entradas, y mezclar clips con y sin
/// audio aborta el montaje. Motion 2.0 no genera audio, asi que el caso normal
/// hoy es video sin pista de sonido.
/// </summary>
public static class VideoAssembler
{
    /// <summary>Resolucion del lienzo cuando no se puede leer la del primer clip.</summary>
    private const int FallbackWidth = 1280;
    private const int FallbackHeight = 720;

    /// <summary>Fps de salida. Fijarlo evita saltos al pegar clips de ritmos distintos.</summary>
    private const int OutputFps = 24;

    public sealed record AssemblyResult(bool Success, string? Error);

    /// <summary>
    /// ¿Esta ffmpeg instalado? Se comprueba antes de montar para poder dar un
    /// error que diga que falta la herramienta, en vez de un "no se pudo iniciar
    /// el proceso" que no le dice nada a quien mira la ejecucion.
    /// </summary>
    public static async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var (exitCode, _, _) = await RunAsync("ffmpeg", ["-version"], ct);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Pega <paramref name="inputPaths"/> en ese orden y escribe el resultado en
    /// <paramref name="outputPath"/>.
    /// </summary>
    public static async Task<AssemblyResult> ConcatAsync(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        CancellationToken ct = default)
    {
        if (inputPaths.Count == 0)
            return new AssemblyResult(false, "No hay clips que montar");

        var missing = inputPaths.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
            return new AssemblyResult(false, $"No se encuentran los clips: {string.Join(", ", missing)}");

        if (!await IsAvailableAsync(ct))
            return new AssemblyResult(false,
                "ffmpeg no esta instalado en el servidor: sin el no se pueden unir los clips. "
                + "Se instala en la imagen Docker (paquete 'ffmpeg').");

        var (width, height) = await ProbeDimensionsAsync(inputPaths[0], ct);
        var withAudio = await AllHaveAudioAsync(inputPaths, ct);

        var args = BuildFfmpegArgs(inputPaths, outputPath, width, height, withAudio);

        var (exitCode, _, stderr) = await RunAsync("ffmpeg", args, ct);

        if (exitCode != 0)
            return new AssemblyResult(false, $"ffmpeg fallo (codigo {exitCode}): {Tail(stderr)}");

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            return new AssemblyResult(false, "ffmpeg termino sin error pero no dejo ningun fichero de salida");

        return new AssemblyResult(true, null);
    }

    /// <summary>
    /// Argumentos del montaje. Cada entrada se escala manteniendo su proporcion,
    /// se rellena con barras hasta el lienzo comun y se normaliza el SAR y el fps;
    /// sin esos tres pasos el filtro concat rechaza entradas heterogeneas.
    /// </summary>
    public static List<string> BuildFfmpegArgs(
        IReadOnlyList<string> inputPaths, string outputPath, int width, int height, bool withAudio)
    {
        var args = new List<string> { "-y" };

        foreach (var path in inputPaths)
        {
            args.Add("-i");
            args.Add(path);
        }

        var filter = new System.Text.StringBuilder();
        for (var i = 0; i < inputPaths.Count; i++)
        {
            filter.Append(CultureInfo.InvariantCulture, $"[{i}:v]");
            filter.Append(CultureInfo.InvariantCulture,
                $"scale={width}:{height}:force_original_aspect_ratio=decrease,");
            filter.Append(CultureInfo.InvariantCulture,
                $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,");
            filter.Append(CultureInfo.InvariantCulture, $"setsar=1,fps={OutputFps}[v{i}];");
        }

        if (withAudio)
            for (var i = 0; i < inputPaths.Count; i++)
                filter.Append(CultureInfo.InvariantCulture, $"[{i}:a]aresample=async=1[a{i}];");

        for (var i = 0; i < inputPaths.Count; i++)
        {
            filter.Append(CultureInfo.InvariantCulture, $"[v{i}]");
            if (withAudio) filter.Append(CultureInfo.InvariantCulture, $"[a{i}]");
        }

        filter.Append(CultureInfo.InvariantCulture,
            $"concat=n={inputPaths.Count}:v=1:a={(withAudio ? 1 : 0)}[outv]");
        if (withAudio) filter.Append("[outa]");

        args.Add("-filter_complex");
        args.Add(filter.ToString());
        args.Add("-map");
        args.Add("[outv]");

        if (withAudio)
        {
            args.Add("-map");
            args.Add("[outa]");
            args.Add("-c:a");
            args.Add("aac");
        }

        args.Add("-c:v");
        args.Add("libx264");
        args.Add("-pix_fmt");
        args.Add("yuv420p");
        // faststart mueve el indice al principio: sin el, un reproductor web tiene
        // que descargar el fichero entero antes de empezar.
        args.Add("-movflags");
        args.Add("+faststart");
        args.Add(outputPath);

        return args;
    }

    /// <summary>Dimensiones del primer clip; si no se pueden leer, el lienzo por defecto.</summary>
    private static async Task<(int Width, int Height)> ProbeDimensionsAsync(string path, CancellationToken ct)
    {
        try
        {
            var (exitCode, stdout, _) = await RunAsync("ffprobe",
            [
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "stream=width,height",
                "-of", "csv=p=0:s=x",
                path,
            ], ct);

            if (exitCode != 0) return (FallbackWidth, FallbackHeight);

            var parts = stdout.Trim().Split('x');
            if (parts.Length >= 2
                && int.TryParse(parts[0], out var w) && w > 0
                && int.TryParse(parts[1], out var h) && h > 0)
            {
                // libx264 exige dimensiones pares.
                return (w % 2 == 0 ? w : w + 1, h % 2 == 0 ? h : h + 1);
            }
        }
        catch
        {
            // Sin ffprobe utilizable se sigue con el lienzo por defecto: el montaje
            // no depende de conocer la resolucion exacta, solo sale peor encuadrado.
        }

        return (FallbackWidth, FallbackHeight);
    }

    private static async Task<bool> AllHaveAudioAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        foreach (var path in paths)
        {
            try
            {
                var (exitCode, stdout, _) = await RunAsync("ffprobe",
                [
                    "-v", "error",
                    "-select_streams", "a",
                    "-show_entries", "stream=index",
                    "-of", "csv=p=0",
                    path,
                ], ct);

                if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                    return false;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName, IEnumerable<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Los dos streams se leen a la vez: ffmpeg escribe mucho por stderr y si
        // no se vacia el buffer el proceso se queda bloqueado esperando sitio.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* el proceso ya se habia ido */ }
            throw;
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>Ultimas lineas de la salida de error: ffmpeg escribe cientos y el motivo real va al final.</summary>
    private static string Tail(string text, int lines = 6)
    {
        if (string.IsNullOrWhiteSpace(text)) return "sin detalle";

        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", all.TakeLast(lines).Select(l => l.Trim()));
    }
}
