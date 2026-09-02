using Server.Services.Ai;
using Xunit;

namespace Server.Tests.DirectorioArchivos;

/// <summary>
/// Reglas del indice del directorio: es obligatorio, tiene que explicar que es
/// cada fichero y tiene que dejar claro de donde se descarga. Un directorio que
/// no cumple las tres cosas no sirve para lo que existe el modulo, asi que se
/// rechaza entero en vez de emitir un indice a medias.
/// </summary>
public class FileDirectoryIndexTests
{
    private const string HostedBase = "https://app.ejemplo.com/api/public/directory/tenant_test/";

    [Fact]
    public void SinIndice_ElDirectorioNoEsValido()
    {
        var result = FileDirectoryIndex.Resolve(null);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("indice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IndiceVacio_NoEsValido()
    {
        var result = FileDirectoryIndex.Resolve("""{ "files": [] }""");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("vacio", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IndiceRoto_SeAvisaEnVezDeIgnorarlo()
    {
        var result = FileDirectoryIndex.Resolve("{ esto no es json ");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("JSON", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FicheroSinDescripcion_SeRechaza()
    {
        var json = """
        {
          "baseUrl": "https://cdn.ejemplo.com/marca",
          "files": [ { "path": "logos/logo.svg" } ]
        }
        """;

        var result = FileDirectoryIndex.Resolve(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("descripcion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FicheroSinRuta_SeRechaza()
    {
        var json = """{ "files": [ { "description": "un fichero cualquiera" } ] }""";

        var result = FileDirectoryIndex.Resolve(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ruta", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FicheroSinRutaAccesible_SeRechaza()
    {
        // Sin baseUrl, sin url propia y sin fichero subido: nadie puede bajarlo.
        var json = """{ "files": [ { "path": "docs/manual.pdf", "description": "manual" } ] }""";

        var result = FileDirectoryIndex.Resolve(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("accesible", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConUrlBase_CadaFicheroCuelgaDeElla()
    {
        var json = """
        {
          "baseUrl": "https://cdn.ejemplo.com/marca/",
          "files": [
            { "path": "logos/primarios/logo.svg", "description": "Logo principal" },
            { "path": "fotos/frontal.jpg", "description": "Foto frontal" }
          ]
        }
        """;

        var result = FileDirectoryIndex.Resolve(json);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(
            "https://cdn.ejemplo.com/marca/logos/primarios/logo.svg",
            result.Entries[0].Url);
        Assert.Equal("logos/primarios", result.Entries[0].Folder);
        Assert.Equal("logo.svg", result.Entries[0].Name);
    }

    [Fact]
    public void UrlPropiaDeLaEntrada_Manda()
    {
        var json = """
        {
          "baseUrl": "https://cdn.ejemplo.com/marca",
          "files": [
            { "path": "externo/tarifa.pdf", "description": "Tarifa", "url": "https://otro.ejemplo.com/tarifa.pdf" }
          ]
        }
        """;

        var result = FileDirectoryIndex.Resolve(json);

        Assert.True(result.IsValid);
        Assert.Equal("https://otro.ejemplo.com/tarifa.pdf", result.Entries[0].Url);
        Assert.Equal(FileDirectoryIndex.Sources.External, result.Entries[0].Source);
    }

    [Fact]
    public void FicheroSubidoAlModulo_SeSirveDesdeNuestraUrlPublica()
    {
        var json = """
        { "files": [ { "path": "manuales/uso.pdf", "description": "Manual de uso" } ] }
        """;

        var result = FileDirectoryIndex.Resolve(
            json,
            configBaseUrl: null,
            hostedFiles: [new FileDirectoryIndex.HostedFile(Guid.NewGuid(), "uso.pdf")],
            hostedUrlFactory: path => HostedBase + path);

        Assert.True(result.IsValid);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(FileDirectoryIndex.Sources.Hosted, entry.Source);
        Assert.Equal(HostedBase + "manuales/uso.pdf", entry.Url);
        Assert.Equal("uso.pdf", entry.SourceFile);
    }

    [Fact]
    public void ElNombreDelFicheroSubidoPuedeDiferirDeLaRutaDelIndice()
    {
        var json = """
        {
          "files": [
            { "path": "manuales/manual-de-uso.pdf", "description": "Manual", "file": "subido.pdf" }
          ]
        }
        """;

        var result = FileDirectoryIndex.Resolve(
            json,
            configBaseUrl: null,
            hostedFiles: [new FileDirectoryIndex.HostedFile(Guid.NewGuid(), "subido.pdf")],
            hostedUrlFactory: path => HostedBase + path);

        Assert.True(result.IsValid);
        Assert.Equal("subido.pdf", result.Entries[0].SourceFile);
        Assert.Equal(HostedBase + "manuales/manual-de-uso.pdf", result.Entries[0].Url);
    }

    [Fact]
    public void RutaQueSaleDelDirectorio_SeRechaza()
    {
        var json = """
        {
          "baseUrl": "https://cdn.ejemplo.com",
          "files": [ { "path": "../secretos/claves.txt", "description": "no deberia colarse" } ]
        }
        """;

        var result = FileDirectoryIndex.Resolve(json);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void RutasRepetidas_SeRechazan()
    {
        var json = """
        {
          "baseUrl": "https://cdn.ejemplo.com",
          "files": [
            { "path": "docs/a.pdf", "description": "uno" },
            { "path": "docs/a.pdf", "description": "otro" }
          ]
        }
        """;

        var result = FileDirectoryIndex.Resolve(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("repetida", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LaListaPelada_TambienEsUnIndiceValido()
    {
        var json = """
        [ { "path": "a.txt", "description": "primero", "url": "https://ejemplo.com/a.txt" } ]
        """;

        var result = FileDirectoryIndex.Resolve(json);

        Assert.True(result.IsValid);
        Assert.Equal("", result.Entries[0].Folder);
    }

    [Theory]
    [InlineData("carpeta\\subcarpeta\\fichero.txt", "carpeta/subcarpeta/fichero.txt")]
    [InlineData("/carpeta//fichero.txt/", "carpeta/fichero.txt")]
    [InlineData("  fichero.txt  ", "fichero.txt")]
    public void LasRutasSeNormalizan(string raw, string expected)
    {
        Assert.Equal(expected, FileDirectoryIndex.NormalizePath(raw));
    }

    [Fact]
    public void LasCarpetasSeDeducenDeLasRutas()
    {
        var json = """
        {
          "baseUrl": "https://cdn.ejemplo.com",
          "files": [
            { "path": "logos/a.svg", "description": "a" },
            { "path": "logos/b.svg", "description": "b" },
            { "path": "fotos/producto/c.jpg", "description": "c" }
          ]
        }
        """;

        var result = FileDirectoryIndex.Resolve(json);

        Assert.True(result.IsValid);
        Assert.Equal(["fotos/producto", "logos"], result.Folders);
    }

    [Fact]
    public void ElIndiceRenderizadoLlevaDescripcionYUrlDeCadaFichero()
    {
        var json = """
        {
          "baseUrl": "https://cdn.ejemplo.com/marca",
          "files": [ { "path": "logos/logo.svg", "description": "Logo principal en vectorial" } ]
        }
        """;

        var result = FileDirectoryIndex.Resolve(json);
        var rendered = FileDirectoryIndex.Render(result, "markdown");

        Assert.Contains("logos", rendered);
        Assert.Contains("Logo principal en vectorial", rendered);
        Assert.Contains("https://cdn.ejemplo.com/marca/logos/logo.svg", rendered);
    }

    [Fact]
    public void ElIndiceEnJson_EsJsonValidoConLasUrlsResueltas()
    {
        var json = """
        {
          "baseUrl": "https://cdn.ejemplo.com",
          "files": [ { "path": "a.txt", "description": "primero" } ]
        }
        """;

        var result = FileDirectoryIndex.Resolve(json);
        var rendered = FileDirectoryIndex.Render(result, "json");

        using var doc = System.Text.Json.JsonDocument.Parse(rendered);
        var files = doc.RootElement.GetProperty("files");
        Assert.Equal(1, files.GetArrayLength());
        Assert.Equal("https://cdn.ejemplo.com/a.txt", files[0].GetProperty("url").GetString());
    }

    [Fact]
    public void LaConfiguracionDelNodoPisaLaDelModulo()
    {
        var moduleConfig = """{ "baseUrl": "https://viejo.ejemplo.com" }""";
        var nodeConfig = """{ "baseUrl": "https://nuevo.ejemplo.com" }""";

        var value = FileDirectoryIndex.ReadConfig(moduleConfig, nodeConfig, FileDirectoryIndex.BaseUrlConfigKey);

        Assert.Equal("https://nuevo.ejemplo.com", value);
    }

    [Fact]
    public void ElIndicePuedeVenirComoObjetoAnidadoEnLaConfiguracion()
    {
        var nodeConfig = """{ "index": { "files": [ { "path": "a.txt", "description": "x" } ] } }""";

        var value = FileDirectoryIndex.ReadConfig(null, nodeConfig, FileDirectoryIndex.IndexConfigKey);

        Assert.NotNull(value);
        Assert.Contains("a.txt", value);
    }

    [Fact]
    public void DosArchivosConElMismoNombreEnCarpetasDistintas_NoSeConfunden()
    {
        // Es lo que produce el explorador al subir el mismo nombre a dos
        // carpetas: cada entrada apunta a SU archivo por id.
        var logoA = new FileDirectoryIndex.HostedFile(Guid.NewGuid(), "logo.png");
        var logoB = new FileDirectoryIndex.HostedFile(Guid.NewGuid(), "logo.png");

        var json = $$"""
        {
          "files": [
            { "path": "marca-a/logo.png", "description": "Logo de la marca A", "fileId": "{{logoA.Id}}" },
            { "path": "marca-b/logo.png", "description": "Logo de la marca B", "fileId": "{{logoB.Id}}" }
          ]
        }
        """;

        var result = FileDirectoryIndex.Resolve(
            json,
            configBaseUrl: null,
            hostedFiles: [logoA, logoB],
            hostedUrlFactory: path => HostedBase + path);

        Assert.True(result.IsValid);
        Assert.Equal(logoA.Id, result.Entries.Single(e => e.Folder == "marca-a").SourceFileId);
        Assert.Equal(logoB.Id, result.Entries.Single(e => e.Folder == "marca-b").SourceFileId);
    }

    [Fact]
    public void UnArchivoSubidoManda_SobreLaUrlBase()
    {
        // El usuario lo puso ahi con el explorador: gana a la baseUrl del
        // repositorio externo, que no tiene por que contener ese archivo.
        var subido = new FileDirectoryIndex.HostedFile(Guid.NewGuid(), "manual.pdf");
        var json = $$"""
        {
          "baseUrl": "https://cdn.ejemplo.com",
          "files": [
            { "path": "docs/manual.pdf", "description": "Manual", "fileId": "{{subido.Id}}" }
          ]
        }
        """;

        var result = FileDirectoryIndex.Resolve(
            json,
            configBaseUrl: null,
            hostedFiles: [subido],
            hostedUrlFactory: path => HostedBase + path);

        Assert.True(result.IsValid);
        Assert.Equal(FileDirectoryIndex.Sources.Hosted, result.Entries[0].Source);
        Assert.Equal(HostedBase + "docs/manual.pdf", result.Entries[0].Url);
    }

    [Fact]
    public void UnFileIdMuerto_SeRescataPorNombreSiSigueSubido()
    {
        // Pasa al resubir un fichero o al copiar el indice de otro nodo: el id
        // cambia pero el nombre no. Antes la entrada se daba por perdida.
        var resubido = new FileDirectoryIndex.HostedFile(Guid.NewGuid(), "manual.pdf");
        var json = """
        {
          "files": [
            { "path": "docs/manual.pdf", "description": "Manual",
              "fileId": "99999999-9999-9999-9999-999999999999" }
          ]
        }
        """;

        var result = FileDirectoryIndex.Resolve(
            json,
            configBaseUrl: null,
            hostedFiles: [resubido],
            hostedUrlFactory: path => HostedBase + path);

        Assert.True(result.IsValid);
        Assert.Equal(FileDirectoryIndex.Sources.Hosted, result.Entries[0].Source);
        Assert.Equal(resubido.Id, result.Entries[0].SourceFileId);
    }

    [Fact]
    public void SinFicherosSubidos_ElErrorLoDiceExplicitamente()
    {
        var json = """{ "files": [ { "path": "a.jpg", "description": "una foto" } ] }""";

        var result = FileDirectoryIndex.Resolve(json, hostedFiles: []);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("no tiene ningun fichero subido", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConFicherosQueNoCasan_ElErrorListaLoQueSiHay()
    {
        // El caso que de verdad despista: hay ficheros, pero son otros. El error
        // tiene que decir cuales, no solo que falta una ruta.
        var json = """{ "files": [ { "path": "no-existe.jpg", "description": "una foto" } ] }""";

        var result = FileDirectoryIndex.Resolve(
            json,
            configBaseUrl: null,
            hostedFiles: [new FileDirectoryIndex.HostedFile(Guid.NewGuid(), "otra.jpg")],
            hostedUrlFactory: path => HostedBase + path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("otra.jpg"));
        Assert.Contains(result.Errors, e => e.Contains("1 fichero(s) subidos"));
    }

    [Fact]
    public void UnFileIdQueYaNoExiste_CaeALaUrlBase()
    {
        // El archivo se borro del nodo: la entrada sigue indexada y se resuelve
        // por la baseUrl en vez de quedarse sin ruta accesible.
        var json = """
        {
          "baseUrl": "https://cdn.ejemplo.com",
          "files": [
            { "path": "docs/manual.pdf", "description": "Manual",
              "fileId": "99999999-9999-9999-9999-999999999999" }
          ]
        }
        """;

        var result = FileDirectoryIndex.Resolve(json, hostedFiles: []);

        Assert.True(result.IsValid);
        Assert.Equal("https://cdn.ejemplo.com/docs/manual.pdf", result.Entries[0].Url);
    }

    [Fact]
    public void LasCarpetasVaciasDeclaradasSeConservan()
    {
        // El explorador guarda las carpetas creadas para que una recien hecha
        // no desaparezca al recargar por no tener archivos todavia.
        var json = """
        {
          "baseUrl": "https://cdn.ejemplo.com",
          "folders": ["logos", "logos/secundarios", "vacia"],
          "files": [ { "path": "logos/a.svg", "description": "a" } ]
        }
        """;

        var result = FileDirectoryIndex.Resolve(json);

        Assert.True(result.IsValid);
        Assert.Equal(["logos", "logos/secundarios", "vacia"], result.Folders);
    }

    [Fact]
    public void UnaCarpetaDeclaradaQueSaleDelDirectorio_SeDescarta()
    {
        var json = """
        {
          "baseUrl": "https://cdn.ejemplo.com",
          "folders": ["../fuera", "dentro"],
          "files": [ { "path": "a.txt", "description": "a" } ]
        }
        """;

        var result = FileDirectoryIndex.Resolve(json);

        Assert.True(result.IsValid);
        Assert.Equal(["dentro"], result.Folders);
    }

    [Fact]
    public void LaRutaPublicaEscapaCadaSegmento()
    {
        var moduleId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var path = FileDirectoryIndex.BuildPublicPath("tenant_test", moduleId, "carpeta con espacios/fichero final.pdf");

        Assert.Equal(
            $"/api/public/directory/tenant_test/{moduleId}/carpeta%20con%20espacios/fichero%20final.pdf",
            path);
    }
}
