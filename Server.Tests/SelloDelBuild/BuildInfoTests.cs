using Server.Services;
using Xunit;

namespace Server.Tests.SelloDelBuild;

/// <summary>
/// Tests del sello del build (commit y hora) que se pinta en el pie del panel
/// izquierdo. Lo que se comprueba es lo que fallaba: que el SHA salga corto y
/// legible, que la hora salga en horario de Madrid y que el dato de entorno gane
/// al del fichero, que puede venir cacheado de un build anterior.
/// </summary>
public class BuildInfoTests
{
    private static readonly bool HasMadridTz = TryFindMadrid();

    private static bool TryFindMadrid()
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    [Fact]
    public void ShortCommit_recorta_el_sha_a_siete_caracteres()
    {
        Assert.Equal("5340776", BuildInfo.ShortCommit("5340776b1c0e4d5a9f3e2b8c7d6a5f4e3b2c1d0a"));
        Assert.Equal("5340776", BuildInfo.ShortCommit("  5340776  "));
    }

    [Fact]
    public void ShortCommit_respeta_lo_que_no_es_un_sha()
    {
        Assert.Equal("unknown", BuildInfo.ShortCommit(null));
        Assert.Equal("unknown", BuildInfo.ShortCommit("   "));
        Assert.Equal("unknown", BuildInfo.ShortCommit("unknown"));
        Assert.Equal("v1.2.3", BuildInfo.ShortCommit("v1.2.3"));
    }

    [Fact]
    public void FormatMadrid_pasa_el_instante_utc_a_hora_de_verano_de_madrid()
    {
        if (!HasMadridTz) return; // entorno sin base de zonas horarias

        // Verano: Madrid va dos horas por delante de UTC.
        Assert.Equal("21/09/2026 16:32", BuildInfo.FormatMadrid("2026-09-21T14:32:05Z"));
    }

    [Fact]
    public void FormatMadrid_pasa_el_instante_utc_a_hora_de_invierno_de_madrid()
    {
        if (!HasMadridTz) return; // entorno sin base de zonas horarias

        // Invierno: una hora por delante de UTC.
        Assert.Equal("15/01/2026 09:05", BuildInfo.FormatMadrid("2026-01-15T08:05:00Z"));
    }

    [Fact]
    public void FormatMadrid_entiende_el_formato_antiguo_con_sufijo_utc()
    {
        if (!HasMadridTz) return; // entorno sin base de zonas horarias

        // Imagenes construidas antes del cambio siguen teniendo este formato.
        Assert.Equal("10/09/2026 14:00", BuildInfo.FormatMadrid("2026-09-10 12:00:00 UTC"));
    }

    [Fact]
    public void FormatMadrid_devuelve_el_texto_original_si_no_es_una_fecha()
    {
        Assert.Equal("unknown", BuildInfo.FormatMadrid("unknown"));
        Assert.Equal("unknown", BuildInfo.FormatMadrid(null));
        Assert.Equal("no-es-una-fecha", BuildInfo.FormatMadrid("no-es-una-fecha"));
    }

    [Fact]
    public void Read_prefiere_el_entorno_al_fichero_cacheado()
    {
        if (!HasMadridTz) return; // entorno sin base de zonas horarias

        using var dir = new TempDir();
        dir.WriteBuildInfo("0000000aaaabbbbccccddddeeeeffff111122223", "2026-01-01T00:00:00Z");

        var info = BuildInfo.Read(dir.Path, name => name switch
        {
            "GIT_COMMIT" => "5340776b1c0e4d5a9f3e2b8c7d6a5f4e3b2c1d0a",
            "BUILD_DATE" => "2026-09-21T14:32:05Z",
            _ => null
        });

        Assert.Equal("5340776", info.CommitHash);
        Assert.Equal("21/09/2026 16:32", info.BuildDate);
    }

    [Fact]
    public void Read_cae_al_fichero_cuando_el_entorno_no_trae_nada_util()
    {
        if (!HasMadridTz) return; // entorno sin base de zonas horarias

        using var dir = new TempDir();
        dir.WriteBuildInfo("5340776b1c0e4d5a9f3e2b8c7d6a5f4e3b2c1d0a", "2026-09-21T14:32:05Z");

        // "unknown" es el valor por defecto del build: no debe tapar al fichero.
        var info = BuildInfo.Read(dir.Path, name => name == "GIT_COMMIT" ? "unknown" : null);

        Assert.Equal("5340776", info.CommitHash);
        Assert.Equal("21/09/2026 16:32", info.BuildDate);
    }

    [Fact]
    public void Read_sin_fichero_ni_entorno_devuelve_unknown()
    {
        using var dir = new TempDir();

        var info = BuildInfo.Read(dir.Path, _ => null);

        Assert.Equal("unknown", info.CommitHash);
        Assert.Equal("unknown", info.BuildDate);
    }

    [Fact]
    public void Read_aguanta_un_build_info_corrupto()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "build-info.json"), "{esto no es json");

        var info = BuildInfo.Read(dir.Path, _ => null);

        Assert.Equal("unknown", info.CommitHash);
        Assert.Equal("unknown", info.BuildDate);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("buildinfo-tests-").FullName;

        public void WriteBuildInfo(string commit, string date) =>
            File.WriteAllText(
                System.IO.Path.Combine(Path, "build-info.json"),
                $"{{\"commitHash\":\"{commit}\",\"buildDate\":\"{date}\"}}");

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
