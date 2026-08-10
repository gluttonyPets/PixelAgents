using Server.Services.Ai;
using Xunit;

namespace Server.Tests.CatalogoModelos;

/// <summary>
/// Los modelos no se borran nunca del catalogo, asi que el aviso de retirada es lo
/// unico que le dice al usuario que un modelo va a dejar de funcionar. Si el aviso
/// se equivoca —de mas o de menos— es peor que no tenerlo.
/// </summary>
public class ModelLifecycleTests
{
    private static readonly DateOnly Hoy = new(2026, 8, 10);

    [Fact]
    public void ModeloSinRetiradaAnunciada_SaleComoActivo()
    {
        var info = ModelLifecycle.Resolve("gpt-5.6-sol", Hoy);

        Assert.Equal(ModelStatus.Active, info.Status);
        Assert.Null(info.ShutdownDate);
        Assert.Null(info.ReplacementId);
    }

    [Fact]
    public void ModeloConFechaFutura_SaleComoDeprecatedConSustituto()
    {
        var info = ModelLifecycle.Resolve("gpt-image-1.5", Hoy);

        Assert.Equal(ModelStatus.Deprecated, info.Status);
        Assert.Equal(new DateOnly(2026, 12, 1), info.ShutdownDate);
        Assert.Equal("gpt-image-2", info.ReplacementId);
        Assert.Equal(113, info.DaysUntilShutdown(Hoy));
    }

    [Fact]
    public void ModeloConFechaPasada_SaleComoRetirado()
    {
        // DALL-E se apago el 12 de mayo de 2026: las llamadas ya fallan.
        var info = ModelLifecycle.Resolve("dall-e-3", Hoy);

        Assert.Equal(ModelStatus.Retired, info.Status);
        Assert.Null(info.DaysUntilShutdown(Hoy));
    }

    [Fact]
    public void SnapshotConFecha_HeredaElEstadoDeSuAlias()
    {
        var info = ModelLifecycle.Resolve("gpt-5-mini-2025-08-07", Hoy);

        Assert.Equal(ModelStatus.Deprecated, info.Status);
        Assert.Equal("gpt-5.6-terra", info.ReplacementId);
        // El id devuelto es el que se pregunto, no el alias.
        Assert.Equal("gpt-5-mini-2025-08-07", info.Id);
    }

    [Fact]
    public void HermanoDeUnModeloRetirado_NoHeredaLaRetirada()
    {
        // El match es exacto o por snapshot, nunca por prefijo libre: si fuese por
        // prefijo, "gpt-5-nano" heredaria la retirada anunciada de "gpt-5" y la UI
        // avisaria de un apagado que nadie ha anunciado.
        Assert.Equal(ModelStatus.Deprecated, ModelLifecycle.Resolve("gpt-5", Hoy).Status);
        Assert.Equal(ModelStatus.Active, ModelLifecycle.Resolve("gpt-5-nano", Hoy).Status);
    }

    [Fact]
    public void AliasVigente_NoHeredaLaRetiradaDeSuSnapshotAntiguo()
    {
        // Solo se retira gpt-4o-2024-05-13; el alias gpt-4o sigue vigente.
        Assert.Equal(ModelStatus.Deprecated, ModelLifecycle.Resolve("gpt-4o-2024-05-13", Hoy).Status);
        Assert.Equal(ModelStatus.Active, ModelLifecycle.Resolve("gpt-4o", Hoy).Status);
    }

    [Theory]
    [InlineData("gpt-5-mini-2025-08-07", "gpt-5-mini")]        // formato OpenAI
    [InlineData("claude-opus-4-5-20251124", "claude-opus-4-5")] // formato Anthropic
    [InlineData("gpt-4o", "gpt-4o")]
    [InlineData("gpt-5.6-sol", "gpt-5.6-sol")]
    [InlineData("grok-4-0709", "grok-4-0709")]                  // 4 digitos no son fecha
    [InlineData("gemini-2.5-flash", "gemini-2.5-flash")]
    public void StripSnapshotSuffix_SoloQuitaSufijosConFormatoDeFecha(string id, string esperado)
    {
        Assert.Equal(esperado, ModelLifecycle.StripSnapshotSuffix(id));
    }

    [Fact]
    public void ModeloVacio_NoRevienta()
    {
        Assert.Equal(ModelStatus.Active, ModelLifecycle.Resolve(null, Hoy).Status);
        Assert.Equal(ModelStatus.Active, ModelLifecycle.Resolve("", Hoy).Status);
    }

    [Fact]
    public void TodoModeloRetiradoDelCatalogoTieneSustituto()
    {
        // Un aviso de "esto ya no funciona" sin decir a que migrar deja al usuario
        // igual de atascado que sin aviso.
        foreach (var model in ModelCatalog.AllModels)
        {
            var info = ModelLifecycle.Resolve(model.Id, Hoy);
            if (info.Status == ModelStatus.Active) continue;

            Assert.False(string.IsNullOrWhiteSpace(info.ReplacementId),
                $"{model.Id} esta {info.Status} y no indica sustituto");
        }
    }

    [Fact]
    public void ElSustitutoRecomendadoSigueEnElCatalogoYNoEstaRetirado()
    {
        foreach (var model in ModelCatalog.AllModels)
        {
            var info = ModelLifecycle.Resolve(model.Id, Hoy);
            if (info.ReplacementId is not { Length: > 0 } replacement) continue;

            Assert.Contains(ModelCatalog.AllModels, m =>
                string.Equals(m.Id, replacement, StringComparison.OrdinalIgnoreCase));

            Assert.NotEqual(ModelStatus.Retired, ModelLifecycle.Resolve(replacement, Hoy).Status);
        }
    }
}
