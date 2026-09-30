using Server.Services.GoogleDrive;
using Xunit;

namespace Server.Tests.Triggers;

/// <summary>Partes sin red del cliente de Drive: consulta, exportacion y lectura del listado.</summary>
public class GoogleDriveServiceTests
{
    [Fact]
    public void LaConsultaPideHijosDirectosSinCarpetasNiPapelera()
    {
        var q = GoogleDriveService.BuildListQuery("abc");
        Assert.Equal("'abc' in parents and trashed = false and mimeType != 'application/vnd.google-apps.folder'", q);
    }

    [Fact]
    public void LaConsultaEscapaComillas() =>
        Assert.StartsWith(@"'a\'b' in parents", GoogleDriveService.BuildListQuery("a'b"));

    [Theory]
    [InlineData("application/vnd.google-apps.document", "application/pdf", ".pdf")]
    [InlineData("application/vnd.google-apps.presentation", "application/pdf", ".pdf")]
    [InlineData("application/vnd.google-apps.spreadsheet", "text/csv", ".csv")]
    [InlineData("application/vnd.google-apps.drawing", "image/png", ".png")]
    public void ExportaLosDocumentosNativos(string mime, string exportMime, string ext) =>
        Assert.Equal((exportMime, ext), GoogleDriveService.ExportFormat(mime));

    [Theory]
    [InlineData("application/vnd.google-apps.form")]
    [InlineData("application/vnd.google-apps.shortcut")]
    public void NoExportaLoQueNoTieneContenido(string mime) =>
        Assert.Null(GoogleDriveService.ExportFormat(mime));

    [Fact]
    public void LeeElListadoYLaPaginacion()
    {
        var json = """
        {
          "nextPageToken": "tok2",
          "files": [
            { "id": "1", "name": "a.pdf", "mimeType": "application/pdf", "createdTime": "2026-09-30T10:00:00.000Z",
              "webViewLink": "https://drive.google.com/file/d/1/view", "size": "1234" },
            { "id": "2", "name": "Doc", "mimeType": "application/vnd.google-apps.document" },
            { "name": "sin id" }
          ]
        }
        """;
        var (files, next) = GoogleDriveService.ParseFileList(json);

        Assert.Equal("tok2", next);
        Assert.Equal(2, files.Count);
        Assert.Equal(new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc), files[0].CreatedTime);
        Assert.Equal(DateTimeKind.Utc, files[0].CreatedTime!.Value.Kind);
        Assert.Equal(1234, files[0].Size);
        Assert.Null(files[1].Size);
        Assert.Null(files[1].CreatedTime);
    }

    [Fact]
    public void SinMasPaginas() =>
        Assert.Null(GoogleDriveService.ParseFileList("""{ "files": [] }""").NextPageToken);

    [Fact]
    public void ElExploradorPideSoloSubcarpetas() =>
        Assert.Equal("'abc' in parents and mimeType = 'application/vnd.google-apps.folder' and trashed = false",
            GoogleDriveService.BuildFolderChildrenQuery("abc"));

    [Fact]
    public void LeeCarpetasYUnidadesCompartidas()
    {
        var (files, next) = GoogleDriveService.ParseFolderList(
            """{ "nextPageToken": "p2", "files": [ { "id": "f1", "name": "Facturas" }, { "name": "sin id" } ] }""", "files");
        Assert.Equal("p2", next);
        var f = Assert.Single(files);
        Assert.Equal(("f1", "Facturas"), (f.Id, f.Name));
        Assert.Empty(f.Parents);

        var (drives, _) = GoogleDriveService.ParseFolderList(
            """{ "drives": [ { "id": "d1", "name": "Equipo" } ] }""", "drives");
        Assert.Equal("Equipo", Assert.Single(drives).Name);
    }

    [Fact]
    public void LeeLosPadresDeCadaCarpeta()
    {
        var (files, _) = GoogleDriveService.ParseFolderList(
            """{ "files": [ { "id": "sub", "name": "Enero", "parents": ["f1"] } ] }""", "files");
        Assert.Equal(["f1"], Assert.Single(files).Parents);
    }

    [Fact]
    public void LaRaizSonLasCarpetasCuyoPadreNoSeVe()
    {
        // La cuenta de servicio ve la carpeta compartida (su padre esta en "Mi unidad" del
        // usuario, invisible), sus subcarpetas y una carpeta de una unidad compartida.
        DriveFolderEntry F(string id, string name, params string[] parents) => new(id, name) { Parents = parents };
        var visibles = new[]
        {
            F("fact", "Facturas", "mi-unidad-del-usuario"),
            F("ene", "Enero", "fact"),
            F("feb", "Febrero", "fact"),
            F("camp", "Campañas", "drv1"),
            F("suelta", "Sin padre"),
        };

        var raiz = GoogleDriveService.RootFolders(visibles, ["drv1"]);

        Assert.Equal(["fact", "suelta"], raiz.Select(f => f.Id));
    }

    [Fact]
    public void UnaCredencialInvalidaDaUnErrorDeDrive()
    {
        var ex = Assert.Throws<GoogleDriveException>(() => GoogleDriveService.ParseCredentials("{}"));
        Assert.DoesNotContain("Search Console", ex.Message);
    }
}
