using System.Diagnostics;
using System.Globalization;

namespace Server.Tests.OrquestadorLoop;

/// <summary>
/// Ficheros de audio y video reales, generados con ffmpeg, para las pruebas que
/// necesitan medir duraciones o montar de verdad. Si ffmpeg no esta instalado
/// (el contenedor de produccion lo trae, una maquina de desarrollo puede no
/// tenerlo) las pruebas que dependen de el terminan sin comprobar nada en vez
/// de fallar por la herramienta.
/// </summary>
internal static class MediaFixtures
{
    private static readonly Lazy<bool> FfmpegAvailable = new(() =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return false;
            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });

    public static bool HasFfmpeg => FfmpegAvailable.Value;

    /// <summary>Audio mudo de <paramref name="seconds"/> segundos.</summary>
    public static string Silence(string dir, string name, double seconds)
    {
        var path = Path.Combine(dir, name);
        Run($"-y -f lavfi -i anullsrc=r=24000:cl=mono -t {Fmt(seconds)} -c:a libmp3lame \"{path}\"");
        return path;
    }

    /// <summary>Clip de video mudo de color liso.</summary>
    public static string Clip(string dir, string name, double seconds)
    {
        var path = Path.Combine(dir, name);
        Run($"-y -f lavfi -i color=c=blue:s=320x240:r=24 -t {Fmt(seconds)} -pix_fmt yuv420p \"{path}\"");
        return path;
    }

    /// <summary>Duracion medida con ffprobe.</summary>
    public static double Duration(string path)
    {
        var output = Capture("ffprobe",
            $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{path}\"");
        return double.Parse(output.Trim(), CultureInfo.InvariantCulture);
    }

    /// <summary>¿Trae el fichero alguna pista de audio?</summary>
    public static bool HasAudio(string path) =>
        Capture("ffprobe", $"-v error -select_streams a -show_entries stream=index -of csv=p=0 \"{path}\"")
            .Trim().Length > 0;

    private static string Fmt(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static void Run(string args)
    {
        var (code, _, err) = Exec("ffmpeg", args);
        if (code != 0) throw new InvalidOperationException($"ffmpeg fallo: {err}");
    }

    private static string Capture(string file, string args) => Exec(file, args).Stdout;

    private static (int Code, string Stdout, string Stderr) Exec(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result, stderr.Result);
    }
}
