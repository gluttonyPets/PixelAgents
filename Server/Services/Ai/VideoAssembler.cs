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
/// audio aborta el montaje. El modulo de video genera los clips sin audio salvo
/// que se pida, asi que el caso normal es video sin pista de sonido.
///
/// Si llega una VOZ EN OFF (uno o varios audios, en orden) esa es la pista del
/// video y manda en la duracion: el video se corta donde acaba la voz y, si los
/// clips se quedan cortos, se congela el ultimo fotograma hasta que termine. Es
/// la diferencia entre entregar 39 s de voz sobre 15 s de imagen (con el final
/// mudo o la voz cortada) y entregar un video que dura lo que dura la locucion.
/// El audio propio de los clips se descarta: debajo de una locucion solo estorba.
/// </summary>
public static class VideoAssembler
{
    /// <summary>Resolucion del lienzo cuando no se puede leer la del primer clip.</summary>
    private const int FallbackWidth = 1280;
    private const int FallbackHeight = 720;

    /// <summary>Fps de salida. Fijarlo evita saltos al pegar clips de ritmos distintos.</summary>
    private const int OutputFps = 24;

    /// <param name="Note">Lo que el montaje tuvo que hacer para cuadrar el video con
    /// la voz (recortar, congelar el final), para dejarlo dicho en el log.</param>
    public sealed record AssemblyResult(bool Success, string? Error, string? Note = null);

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
    public static Task<AssemblyResult> ConcatAsync(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        CancellationToken ct = default) =>
        ConcatAsync(inputPaths, [], outputPath, ct);

    /// <summary>
    /// Pega <paramref name="inputPaths"/> en ese orden y, si hay
    /// <paramref name="voicePaths"/>, los usa (tambien en orden) como pista de
    /// audio y ajusta la duracion del video a la de la voz.
    /// </summary>
    public static async Task<AssemblyResult> ConcatAsync(
        IReadOnlyList<string> inputPaths,
        IReadOnlyList<string> voicePaths,
        string outputPath,
        CancellationToken ct = default)
    {
        if (inputPaths.Count == 0)
            return new AssemblyResult(false, "No hay clips que montar");

        var missing = inputPaths.Concat(voicePaths).Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
            return new AssemblyResult(false, $"No se encuentran los ficheros: {string.Join(", ", missing)}");

        if (!await IsAvailableAsync(ct))
            return new AssemblyResult(false,
                "ffmpeg no esta instalado en el servidor: sin el no se pueden unir los clips. "
                + "Se instala en la imagen Docker (paquete 'ffmpeg').");

        var (width, height) = await ProbeDimensionsAsync(inputPaths[0], ct);

        List<string> args;
        string? note = null;

        if (voicePaths.Count > 0)
        {
            var voiceSeconds = await SumDurationsAsync(voicePaths, ct);
            var clipSeconds = await SumDurationsAsync(inputPaths, ct);
            note = DescribeFit(voiceSeconds, clipSeconds);
            args = BuildFfmpegArgsWithVoice(inputPaths, voicePaths, outputPath, width, height, voiceSeconds);
        }
        else
        {
            var withAudio = await AllHaveAudioAsync(inputPaths, ct);
            args = BuildFfmpegArgs(inputPaths, outputPath, width, height, withAudio);
        }

        var (exitCode, _, stderr) = await RunAsync("ffmpeg", args, ct);

        if (exitCode != 0)
            return new AssemblyResult(false, $"ffmpeg fallo (codigo {exitCode}): {Tail(stderr)}");

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            return new AssemblyResult(false, "ffmpeg termino sin error pero no dejo ningun fichero de salida");

        return new AssemblyResult(true, null, note);
    }

    /// <summary>
    /// Duracion de un fichero de audio o video en segundos, o null si ffprobe no
    /// la sabe leer (o no esta instalado). La usan el montaje y el orquestador
    /// loop, que decide cuantas partes planificar a partir de ella.
    /// </summary>
    public static async Task<double?> ProbeDurationAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var (exitCode, stdout, _) = await RunAsync("ffprobe",
            [
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                path,
            ], ct);

            if (exitCode != 0) return null;

            return double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                   && seconds > 0
                ? seconds
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Suma de duraciones; null si alguna no se puede leer, porque una suma
    /// parcial seria peor que ninguna: cortaria la voz antes de tiempo.</summary>
    private static async Task<double?> SumDurationsAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var total = 0d;
        foreach (var path in paths)
        {
            var seconds = await ProbeDurationAsync(path, ct);
            if (seconds is null) return null;
            total += seconds.Value;
        }

        return total;
    }

    /// <summary>Frase para el log con lo que hubo que hacer para cuadrar video y voz.</summary>
    public static string? DescribeFit(double? voiceSeconds, double? clipSeconds)
    {
        if (voiceSeconds is null)
            return "No se pudo medir la voz: el video se corta donde acabe la pista mas corta";

        if (clipSeconds is null)
            return $"La voz dura {voiceSeconds.Value.ToString("0.#", CultureInfo.InvariantCulture)} s y el video se ajusta a ella";

        var diff = clipSeconds.Value - voiceSeconds.Value;
        var voice = voiceSeconds.Value.ToString("0.#", CultureInfo.InvariantCulture);
        var clips = clipSeconds.Value.ToString("0.#", CultureInfo.InvariantCulture);
        var gap = Math.Abs(diff).ToString("0.#", CultureInfo.InvariantCulture);

        return Math.Abs(diff) < 0.05
            ? $"La voz ({voice} s) y los clips ({clips} s) duran lo mismo"
            : diff > 0
                ? $"Los clips duran {clips} s y la voz {voice} s: se recortan {gap} s del final del video"
                : $"Los clips duran {clips} s y la voz {voice} s: se congela el ultimo fotograma {gap} s para no cortar la voz";
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

    /// <summary>
    /// Argumentos del montaje con voz en off. El video se monta igual que sin voz,
    /// pero despues se alarga congelando el ultimo fotograma (<c>tpad</c>) tanto
    /// como dure la voz y se corta a esa duracion (<c>-t</c>): las dos cosas a la
    /// vez cubren los dos casos, clips mas largos y clips mas cortos que la voz,
    /// sin tener que saber cuanto duran los clips. Las voces se pegan en orden y
    /// son la unica pista de audio.
    ///
    /// Si no se pudo medir la voz no hay a que duracion cortar, y se recurre a
    /// <c>-shortest</c>, que al menos no deja ni imagen sin voz ni voz sin imagen.
    /// </summary>
    public static List<string> BuildFfmpegArgsWithVoice(
        IReadOnlyList<string> clipPaths, IReadOnlyList<string> voicePaths, string outputPath,
        int width, int height, double? voiceSeconds)
    {
        var args = new List<string> { "-y" };

        foreach (var path in clipPaths.Concat(voicePaths))
        {
            args.Add("-i");
            args.Add(path);
        }

        var filter = new System.Text.StringBuilder();
        for (var i = 0; i < clipPaths.Count; i++)
        {
            filter.Append(CultureInfo.InvariantCulture, $"[{i}:v]");
            filter.Append(CultureInfo.InvariantCulture,
                $"scale={width}:{height}:force_original_aspect_ratio=decrease,");
            filter.Append(CultureInfo.InvariantCulture,
                $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,");
            filter.Append(CultureInfo.InvariantCulture, $"setsar=1,fps={OutputFps}[v{i}];");
        }

        for (var i = 0; i < clipPaths.Count; i++)
            filter.Append(CultureInfo.InvariantCulture, $"[v{i}]");

        if (voiceSeconds is double total)
        {
            filter.Append(CultureInfo.InvariantCulture, $"concat=n={clipPaths.Count}:v=1:a=0[cv];");
            filter.Append(CultureInfo.InvariantCulture,
                $"[cv]tpad=stop_mode=clone:stop_duration={total.ToString("0.###", CultureInfo.InvariantCulture)}[outv];");
        }
        else
        {
            filter.Append(CultureInfo.InvariantCulture, $"concat=n={clipPaths.Count}:v=1:a=0[outv];");
        }

        for (var j = 0; j < voicePaths.Count; j++)
            filter.Append(CultureInfo.InvariantCulture, $"[{clipPaths.Count + j}:a]aresample=async=1[va{j}];");

        if (voicePaths.Count == 1)
        {
            filter.Append("[va0]anull[outa]");
        }
        else
        {
            for (var j = 0; j < voicePaths.Count; j++)
                filter.Append(CultureInfo.InvariantCulture, $"[va{j}]");
            filter.Append(CultureInfo.InvariantCulture, $"concat=n={voicePaths.Count}:v=0:a=1[outa]");
        }

        args.Add("-filter_complex");
        args.Add(filter.ToString());
        args.Add("-map");
        args.Add("[outv]");
        args.Add("-map");
        args.Add("[outa]");
        args.Add("-c:v");
        args.Add("libx264");
        args.Add("-pix_fmt");
        args.Add("yuv420p");
        args.Add("-c:a");
        args.Add("aac");

        if (voiceSeconds is double seconds)
        {
            args.Add("-t");
            args.Add(seconds.ToString("0.###", CultureInfo.InvariantCulture));
        }
        else
        {
            args.Add("-shortest");
        }

        args.Add("-movflags");
        args.Add("+faststart");
        args.Add(outputPath);

        return args;
    }

    /// <summary>Dimensiones del primer clip; si no se pueden leer, el lienzo por defecto.</summary>
    internal static async Task<(int Width, int Height)> ProbeDimensionsAsync(string path, CancellationToken ct)
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

    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName, IEnumerable<string> arguments, CancellationToken ct, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;

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
    internal static string Tail(string text, int lines = 6)
    {
        if (string.IsNullOrWhiteSpace(text)) return "sin detalle";

        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", all.TakeLast(lines).Select(l => l.Trim()));
    }
}
