namespace Server.Services.Ai;

/// <summary>
/// La parte de ffmpeg de los subtitulos: sacar el audio del video (para pedir los
/// tiempos de cada palabra) y pintar el fichero ASS sobre el video (filtro
/// <c>subtitles</c>, que usa libass).
///
/// Todo se hace en una carpeta temporal y ffmpeg arranca dentro de ella, asi el
/// filtro recibe nombres relativos sin rutas: en un filtro de ffmpeg los dos
/// puntos, comillas y comas de una ruta hay que escaparlos, y un fallo ahi sale
/// como un error de sintaxis del filtro, no de fichero.
/// </summary>
public static class SubtitleBurner
{
    /// <summary>Carpeta con la fuente de los subtitulos (Poppins ExtraBold, licencia OFL).</summary>
    public static string FontsDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");

    /// <summary>
    /// Audio del video en mp3 mono (para la alineacion basta y pesa poco), o null
    /// si el video no tiene pista de audio.
    /// </summary>
    public static async Task<(byte[]? Audio, string? Error)> ExtractAudioAsync(string videoPath, string workDir, CancellationToken ct)
    {
        var output = Path.Combine(workDir, "audio.mp3");
        var (exitCode, _, stderr) = await VideoAssembler.RunAsync("ffmpeg",
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", videoPath,
            "-vn", "-ac", "1", "-ar", "22050", "-b:a", "64k",
            output,
        ], ct);

        if (exitCode != 0 || !File.Exists(output))
            return (null, stderr.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase)
                          || stderr.Contains("Output file is empty", StringComparison.OrdinalIgnoreCase)
                ? "El video no tiene audio: no hay voz de la que sacar los subtitulos."
                : $"No se pudo sacar el audio del video: {VideoAssembler.Tail(stderr)}");

        var bytes = await File.ReadAllBytesAsync(output, ct);
        return bytes.Length == 0
            ? (null, "El video no tiene audio: no hay voz de la que sacar los subtitulos.")
            : (bytes, null);
    }

    /// <summary>
    /// Pinta <paramref name="ass"/> sobre el video y deja el resultado en
    /// <paramref name="outputPath"/>. El audio se copia tal cual.
    /// </summary>
    public static async Task<string?> BurnAsync(
        string videoPath, string ass, string workDir, string outputPath, CancellationToken ct)
    {
        await File.WriteAllTextAsync(Path.Combine(workDir, "subs.ass"), ass, ct);

        // La fuente va a una carpeta junto al .ass. Si no estuviera, libass tira de
        // las del sistema y el video sale igual, con otra letra.
        var fonts = Path.Combine(workDir, "fonts");
        Directory.CreateDirectory(fonts);
        if (Directory.Exists(FontsDirectory))
            foreach (var font in Directory.EnumerateFiles(FontsDirectory, "*.ttf"))
                File.Copy(font, Path.Combine(fonts, Path.GetFileName(font)), overwrite: true);

        var (exitCode, _, stderr) = await VideoAssembler.RunAsync("ffmpeg",
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", videoPath,
            "-vf", "subtitles=subs.ass:fontsdir=fonts",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "copy",
            "-movflags", "+faststart",
            outputPath,
        ], ct, workingDirectory: workDir);

        return exitCode == 0 && File.Exists(outputPath)
            ? null
            : $"ffmpeg no pudo poner los subtitulos: {VideoAssembler.Tail(stderr)}";
    }
}
