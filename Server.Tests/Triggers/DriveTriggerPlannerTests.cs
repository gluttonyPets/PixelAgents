using Server.Services.GoogleDrive;
using Server.Services.Triggers;
using Xunit;

namespace Server.Tests.Triggers;

/// <summary>
/// Decision del trigger de Drive: la primera consulta (o una carpeta nueva) solo
/// toma nota de lo que hay; despues dispara una vez por cada archivo no visto.
/// </summary>
public class DriveTriggerPlannerTests
{
    private static readonly string Key = DriveTriggerPlanner.ConfigKey("carpeta1");

    private static DriveFile File(string id, int minutos) =>
        new(id, $"{id}.pdf", "application/pdf", new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc).AddMinutes(minutos), null, 100);

    private static HashSet<string> Vistos(params string[] ids) =>
        ids.Select(DriveTriggerPlanner.ItemKey).ToHashSet();

    [Fact]
    public void LaPrimeraConsultaSoloTomaLineaBase()
    {
        var plan = DriveTriggerPlanner.Plan(null, hasBaseline: false, Key, [File("a", 0), File("b", 1)], Vistos());
        Assert.True(plan.TakeBaseline);
        Assert.Empty(plan.NewFiles);
    }

    [Fact]
    public void OtraCarpetaRehaceLaLineaBase()
    {
        var plan = DriveTriggerPlanner.Plan(DriveTriggerPlanner.ConfigKey("otra"), hasBaseline: true, Key,
            [File("a", 0)], Vistos());
        Assert.True(plan.TakeBaseline);
    }

    [Fact]
    public void DisparaSoloConLosNoVistos_DelMasAntiguoAlMasNuevo()
    {
        var plan = DriveTriggerPlanner.Plan(Key, hasBaseline: true, Key,
            [File("c", 5), File("a", 0), File("b", 2)], Vistos("a"));
        Assert.False(plan.TakeBaseline);
        Assert.Equal(["b", "c"], plan.NewFiles.Select(f => f.Id));
    }

    [Fact]
    public void SinNovedadesNoDispara()
    {
        var plan = DriveTriggerPlanner.Plan(Key, hasBaseline: true, Key, [File("a", 0)], Vistos("a"));
        Assert.False(plan.TakeBaseline);
        Assert.Empty(plan.NewFiles);
    }

    [Fact]
    public void UnaSubidaMasivaSeRepartePorConsultas()
    {
        var muchos = Enumerable.Range(0, 25).Select(i => File($"f{i:00}", i)).ToList();
        var plan = DriveTriggerPlanner.Plan(Key, hasBaseline: true, Key, muchos, Vistos());
        Assert.Equal(DriveTriggerPlanner.MaxFilesPerCheck, plan.NewFiles.Count);
        Assert.Equal("f00", plan.NewFiles[0].Id);
    }

    [Fact]
    public void ElTextoDeEntradaDescribeElArchivo()
    {
        var file = new DriveFile("id123", "factura.pdf", "application/pdf",
            new DateTime(2026, 9, 30, 8, 15, 0, DateTimeKind.Utc), "https://drive.google.com/file/d/id123/view", 2048);
        var text = DriveTriggerPlanner.BuildUserInput(file, "Facturas", "(nota)");

        Assert.Contains("Nuevo archivo en Google Drive: factura.pdf", text);
        Assert.Contains("Carpeta: Facturas", text);
        Assert.Contains("Tipo: application/pdf", text);
        Assert.Contains("Creado: 2026-09-30 08:15 UTC", text);
        Assert.Contains("Enlace: https://drive.google.com/file/d/id123/view", text);
        Assert.Contains("ID: id123", text);
        Assert.EndsWith("(nota)", text);
    }
}
