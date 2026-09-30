using Server.Services.Triggers;
using Xunit;

namespace Server.Tests.Triggers;

/// <summary>
/// Tipo de pipeline: un pipeline Trigger llama "Trigger" a su modulo de entrada
/// para distinguirlo a simple vista, sin pisar un nombre puesto por el usuario.
/// </summary>
public class ProjectTypesTests
{
    [Theory]
    [InlineData(null, "Normal")]
    [InlineData("", "Normal")]
    [InlineData("loquesea", "Normal")]
    [InlineData("Normal", "Normal")]
    [InlineData("Trigger", "Trigger")]
    public void NormalizaElTipo(string? tipo, string esperado) =>
        Assert.Equal(esperado, ProjectTypes.Normalize(tipo));

    [Fact]
    public void ElModuloDeEntradaSeLlamaSegunElTipo()
    {
        Assert.Equal("Inicio", ProjectTypes.EntryStepName(ProjectTypes.Normal));
        Assert.Equal("Trigger", ProjectTypes.EntryStepName(ProjectTypes.Trigger));
    }

    [Theory]
    [InlineData("Inicio", "Trigger", "Trigger")]
    [InlineData(null, "Trigger", "Trigger")]
    [InlineData("Trigger", "Normal", "Inicio")]
    [InlineData("Inicio", "Normal", "Inicio")]
    public void RenombraElNombrePorDefecto(string? actual, string nuevoTipo, string esperado) =>
        Assert.Equal(esperado, ProjectTypes.RenameEntryStep(actual, nuevoTipo));

    [Fact]
    public void RespetaUnNombrePuestoAMano() =>
        Assert.Equal("Entrada de facturas", ProjectTypes.RenameEntryStep("Entrada de facturas", ProjectTypes.Trigger));

    [Fact]
    public void ElCatalogoDeTriggersIncluyeDrive()
    {
        Assert.True(TriggerTypes.IsValid(TriggerTypes.DriveNewFile));
        Assert.False(TriggerTypes.IsValid("Inventado"));
        Assert.False(TriggerTypes.IsValid(null));
    }
}
