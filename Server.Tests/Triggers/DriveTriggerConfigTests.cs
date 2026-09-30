using Server.Services.Triggers;
using Xunit;

namespace Server.Tests.Triggers;

/// <summary>
/// Configuracion del trigger de Drive: el usuario pega la URL de la carpeta tal
/// como la copia del navegador, y el intervalo se acota a un rango razonable.
/// </summary>
public class DriveTriggerConfigTests
{
    private const string Id = "1AbCdEfGhIjKlMnOpQrStUvWxYz012345";

    [Theory]
    [InlineData("https://drive.google.com/drive/folders/" + Id)]
    [InlineData("https://drive.google.com/drive/folders/" + Id + "?usp=sharing")]
    [InlineData("https://drive.google.com/drive/u/0/folders/" + Id)]
    [InlineData("https://drive.google.com/open?id=" + Id)]
    [InlineData(Id)]
    [InlineData("  " + Id + "  ")]
    public void ExtraeElIdDeLaCarpeta(string carpeta) =>
        Assert.Equal(Id, DriveTriggerConfig.ExtractFolderId(carpeta));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mi unidad/Facturas")]
    [InlineData("https://example.com/otra-cosa")]
    public void NoReconoceLoQueNoEsUnaCarpeta(string? carpeta) =>
        Assert.Null(DriveTriggerConfig.ExtractFolderId(carpeta));

    [Fact]
    public void ValidaQueEsteCompleta()
    {
        Assert.NotNull(new DriveTriggerConfig { Folder = Id }.Validate());
        Assert.NotNull(new DriveTriggerConfig { ApiKeyId = Guid.NewGuid() }.Validate());
        Assert.NotNull(new DriveTriggerConfig { ApiKeyId = Guid.NewGuid(), Folder = "no es una carpeta" }.Validate());
        Assert.Null(new DriveTriggerConfig { ApiKeyId = Guid.NewGuid(), Folder = Id }.Validate());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 5)]
    [InlineData(100000, 1440)]
    public void AcotaElIntervalo(int minutos, int esperado) =>
        Assert.Equal(TimeSpan.FromMinutes(esperado), new DriveTriggerConfig { PollMinutes = minutos }.PollInterval);

    [Fact]
    public void IdaYVueltaPorJson()
    {
        var key = Guid.NewGuid();
        var json = new DriveTriggerConfig { ApiKeyId = key, Folder = Id, FolderName = "Equipo / Facturas", PollMinutes = 15, DownloadFile = false }.Serialize();
        Assert.DoesNotContain("PollInterval", json);
        Assert.DoesNotContain("FolderId", json);
        var back = DriveTriggerConfig.Parse(json);
        Assert.Equal(key, back.ApiKeyId);
        Assert.Equal(Id, back.Folder);
        Assert.Equal("Equipo / Facturas", back.FolderName);
        Assert.Equal(15, back.PollMinutes);
        Assert.False(back.DownloadFile);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{no json")]
    public void UnaConfiguracionIlegibleEsLaPorDefecto(string? json)
    {
        var cfg = DriveTriggerConfig.Parse(json);
        Assert.Null(cfg.ApiKeyId);
        Assert.Equal(DriveTriggerConfig.DefaultPollMinutes, cfg.PollMinutes);
        Assert.True(cfg.DownloadFile);
    }
}
