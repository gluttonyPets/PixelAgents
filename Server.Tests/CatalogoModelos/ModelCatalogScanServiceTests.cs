using Microsoft.EntityFrameworkCore;
using Moq;
using Server.Data;
using Server.Models;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.CatalogoModelos;

/// <summary>
/// El servicio de deteccion es lo unico que sabe que un precio ha cambiado: el
/// catalogo compilado dice cuanto cuesta hoy, nunca cuanto costaba antes. Si el
/// diff se equivoca, el historico miente y no hay forma de darse cuenta.
/// </summary>
public class ModelCatalogScanServiceTests
{
    private static UserDbContext Db(string name) =>
        new(new DbContextOptionsBuilder<UserDbContext>().UseInMemoryDatabase(name).Options);

    /// <summary>
    /// Escaneo sin proveedores: <paramref name="upstream"/> null simula "no se ha
    /// podido preguntar", que es lo normal en un tenant sin API keys.
    /// </summary>
    private static ModelCatalogScanService Service(
        string? provider = null, IReadOnlySet<string>? upstream = null)
    {
        var mock = new Mock<IModelAvailabilityService>();

        mock.Setup(a => a.GetAvailableModelIdsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string p, string _, CancellationToken _) =>
                provider is not null && string.Equals(p, provider, StringComparison.OrdinalIgnoreCase)
                    ? upstream
                    : null);

        return new ModelCatalogScanService(mock.Object);
    }

    private static void AddKey(UserDbContext db, string provider)
    {
        db.ApiKeys.Add(new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = provider,
            ProviderType = provider,
            EncryptedKey = "sk-test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task LaPrimeraPasadaSoloTomaLaFotoYNoGeneraHistorico()
    {
        // Sin foto previa, todo el catalogo seria "modelo nuevo": el historico naceria
        // con 72 entradas inutiles y nadie volveria a mirarlo.
        await using var db = Db(nameof(LaPrimeraPasadaSoloTomaLaFotoYNoGeneraHistorico));

        var result = await Service().RunScanAsync(db);

        Assert.True(result.Run.IsBaseline);
        Assert.Empty(result.Changes);
        Assert.Equal(ModelCatalog.AllModels.Length, result.Run.ModelsScanned);
        Assert.Equal(ModelCatalog.AllModels.Length, await db.ModelCatalogSnapshots.CountAsync());
        Assert.Empty(await db.ModelCatalogChanges.ToListAsync());
    }

    [Fact]
    public async Task SinCambiosLaSegundaPasadaNoApuntaNada()
    {
        await using var db = Db(nameof(SinCambiosLaSegundaPasadaNoApuntaNada));
        var svc = Service();

        await svc.RunScanAsync(db);
        var second = await svc.RunScanAsync(db);

        Assert.False(second.Run.IsBaseline);
        Assert.Empty(second.Changes);
        Assert.Equal(0, second.Run.ChangesDetected);

        // Pero la pasada queda registrada: "se miro y no habia nada" es informacion.
        Assert.Equal(2, await db.ModelScanRuns.CountAsync());
    }

    [Fact]
    public async Task DetectaUnaSubidaDePrecioConElAntesYElDespues()
    {
        await using var db = Db(nameof(DetectaUnaSubidaDePrecioConElAntesYElDespues));
        var svc = Service();

        await svc.RunScanAsync(db);

        // Se abarata la foto para que el catalogo actual parezca una subida. Es lo
        // mismo que pasa de verdad cuando alguien revisa PricingCatalog.cs y despliega.
        var snap = await db.ModelCatalogSnapshots.FirstAsync(s => s.ModelId == "gpt-5.6-sol");
        var real = snap.InputPerMTok;
        snap.InputPerMTok = 1.00m;
        await db.SaveChangesAsync();

        var result = await svc.RunScanAsync(db);

        var change = Assert.Single(result.Changes, c => c.ModelId == "gpt-5.6-sol");
        Assert.Equal("price_change", change.ChangeType);
        Assert.Equal("InputPerMTok", change.Field);
        Assert.Equal("$1", change.OldValue);
        Assert.Equal("$" + real!.Value.ToString("0.####",
            System.Globalization.CultureInfo.InvariantCulture), change.NewValue);
        Assert.Equal(1, result.Run.PriceChanges);

        // Y la foto queda al dia: repetir el escaneo no vuelve a cantar el mismo cambio.
        var after = await svc.RunScanAsync(db);
        Assert.Empty(after.Changes);
    }

    [Fact]
    public async Task ElPorcentajeDeLaSubidaVaEnLaNota()
    {
        // La cifra suelta no dice nada: pasar de $0,20 a $0,25 solo se entiende
        // cuando se lee que es un 25% mas.
        await using var db = Db(nameof(ElPorcentajeDeLaSubidaVaEnLaNota));
        var svc = Service();

        await svc.RunScanAsync(db);

        var snap = await db.ModelCatalogSnapshots.FirstAsync(s => s.ModelId == "gpt-5.6-sol");
        snap.OutputPerMTok = 15.00m;   // el catalogo dice 30: es el doble
        await db.SaveChangesAsync();

        var result = await svc.RunScanAsync(db);
        var change = Assert.Single(result.Changes, c => c.Field == "OutputPerMTok");

        Assert.Contains("+100%", change.Note);
    }

    [Fact]
    public async Task ApuntaLosModelosQueElProveedorListaYElCatalogoNoTiene()
    {
        await using var db = Db(nameof(ApuntaLosModelosQueElProveedorListaYElCatalogoNoTiene));
        AddKey(db, "OpenAI");

        var upstream = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gpt-5.6-sol",          // ya esta en el catalogo
            "gpt-5.6-sol-2026-03-11", // snapshot con fecha del anterior: no es nuevo
            "gpt-9-inventado",      // este si es nuevo
        };

        var svc = Service("OpenAI", upstream);
        await svc.RunScanAsync(db);          // foto inicial
        var result = await svc.RunScanAsync(db);

        // La primera pasada ya se lo guardo, asi que en la segunda no vuelve a salir.
        Assert.DoesNotContain(result.Changes, c => c.ModelId == "gpt-9-inventado");
        Assert.Contains(await db.ModelCatalogSnapshots.ToListAsync(),
            s => s.ModelId == "gpt-9-inventado" && s.Source == "provider");
    }

    [Fact]
    public async Task UnModeloNuevoDelProveedorSaleUnaVezYSoloUna()
    {
        await using var db = Db(nameof(UnModeloNuevoDelProveedorSaleUnaVezYSoloUna));
        AddKey(db, "OpenAI");

        // Primera pasada sin novedades para dejar la foto hecha.
        await Service("OpenAI", new HashSet<string> { "gpt-5.6-sol" }).RunScanAsync(db);

        var conNovedad = Service("OpenAI",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "gpt-5.6-sol", "gpt-9-inventado" });

        var primera = await conNovedad.RunScanAsync(db);
        var change = Assert.Single(primera.Changes, c => c.ModelId == "gpt-9-inventado");
        Assert.Equal("provider_new_model", change.ChangeType);
        Assert.Contains("ModelCatalog.cs", change.Note);

        // Sigue sin estar en el catalogo, pero ya no es una novedad de hoy: repetirlo
        // en cada pasada llenaria el historico de la misma linea.
        var segunda = await conNovedad.RunScanAsync(db);
        Assert.DoesNotContain(segunda.Changes, c => c.ModelId == "gpt-9-inventado");
    }

    [Fact]
    public async Task AvisaCuandoElProveedorDejaDeListarUnModeloDelCatalogo()
    {
        await using var db = Db(nameof(AvisaCuandoElProveedorDejaDeListarUnModeloDelCatalogo));
        AddKey(db, "OpenAI");

        var todos = ModelCatalog.AllModels
            .Where(m => m.Provider == "OpenAI")
            .Select(m => m.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        await Service("OpenAI", todos).RunScanAsync(db);

        var menosUno = new HashSet<string>(todos, StringComparer.OrdinalIgnoreCase);
        menosUno.Remove("gpt-5.6-sol");

        var result = await Service("OpenAI", menosUno).RunScanAsync(db);

        var change = Assert.Single(result.Changes,
            c => c.ModelId == "gpt-5.6-sol" && c.ChangeType == "availability_change");
        Assert.Equal("false", change.NewValue);
    }

    [Fact]
    public async Task UnaConsultaFallidaNoMarcaElCatalogoComoRetirado()
    {
        // null es "hoy no he podido preguntar", no "el proveedor no lo tiene". Si se
        // confundieran, un corte de red pintaria todo el catalogo de rojo.
        await using var db = Db(nameof(UnaConsultaFallidaNoMarcaElCatalogoComoRetirado));
        AddKey(db, "OpenAI");

        var todos = ModelCatalog.AllModels
            .Where(m => m.Provider == "OpenAI")
            .Select(m => m.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        await Service("OpenAI", todos).RunScanAsync(db);

        // Ahora el proveedor no responde.
        var result = await Service().RunScanAsync(db);

        Assert.DoesNotContain(result.Changes, c => c.ChangeType == "availability_change");
        Assert.All(await db.ModelCatalogSnapshots.Where(s => s.Provider == "OpenAI").ToListAsync(),
            s => Assert.True(s.AvailableUpstream));
    }

    [Fact]
    public async Task UnModeloQueDesapareceDelCatalogoSeApuntaComoBaja()
    {
        await using var db = Db(nameof(UnModeloQueDesapareceDelCatalogoSeApuntaComoBaja));
        var svc = Service();
        await svc.RunScanAsync(db);

        // Una foto de un modelo que el catalogo ya no tiene: es lo que queda cuando
        // alguien borra una entrada de ModelCatalog.cs.
        db.ModelCatalogSnapshots.Add(new ModelCatalogSnapshot
        {
            Id = Guid.NewGuid(),
            ModelId = "gpt-4-turbo",
            Provider = "OpenAI",
            DisplayName = "GPT-4 Turbo",
            Kind = "text",
            InputPerMTok = 10m,
            OutputPerMTok = 30m,
            Source = "catalog",
        });
        await db.SaveChangesAsync();

        var result = await svc.RunScanAsync(db);

        var change = Assert.Single(result.Changes, c => c.ModelId == "gpt-4-turbo");
        Assert.Equal("removed_model", change.ChangeType);
        Assert.Null(await db.ModelCatalogSnapshots.FirstOrDefaultAsync(s => s.ModelId == "gpt-4-turbo"));
    }

    [Fact]
    public async Task ElHistoricoDevuelveLoMasRecienteArriba()
    {
        await using var db = Db(nameof(ElHistoricoDevuelveLoMasRecienteArriba));
        var svc = Service();

        await svc.RunScanAsync(db);
        await svc.RunScanAsync(db);

        var history = await svc.GetHistoryAsync(db);

        Assert.Equal(2, history.Runs.Count);
        Assert.NotNull(history.LastRun);
        Assert.False(history.LastRun!.IsBaseline);
        Assert.True(history.Runs[0].StartedAt >= history.Runs[1].StartedAt);
    }

    [Fact]
    public async Task LaFotoGuardaLasMismasTarifasQueLaPantalla()
    {
        // Si el escaneo leyese el precio por otro camino que la pantalla, detectaria
        // cambios que la tabla no muestra y al reves.
        await using var db = Db(nameof(LaFotoGuardaLasMismasTarifasQueLaPantalla));
        await Service().RunScanAsync(db);

        foreach (var model in ModelCatalog.AllModels)
        {
            var rates = ModelCatalogService.ResolveRates(model);
            var snap = await db.ModelCatalogSnapshots.FirstAsync(s => s.ModelId == model.Id);

            Assert.Equal(rates.Kind, snap.Kind);
            Assert.Equal(rates.InputPerMTok, snap.InputPerMTok);
            Assert.Equal(rates.OutputPerMTok, snap.OutputPerMTok);
            Assert.Equal(rates.ImageMedium, snap.ImageMedium);
            Assert.Equal(rates.AuxAmount, snap.AuxAmount);
        }
    }
}
