using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;

namespace Server.Services.Ai;

public interface IModelCatalogService
{
    /// <summary>Estado de retirada y disponibilidad de cada modelo del catalogo.</summary>
    Task<List<ModelLifecycleResponse>> GetLifecycleAsync(UserDbContext db, CancellationToken ct = default);

    /// <summary>Lo anterior mas las tarifas, para la pantalla de precios.</summary>
    Task<List<ModelPriceResponse>> GetPricingAsync(UserDbContext db, CancellationToken ct = default);
}

/// <summary>
/// Une las tres fuentes que describen un modelo —catalogo, tarifas y ciclo de vida—
/// y las resuelve contra las API keys del tenant. Vive fuera de Program.cs porque lo
/// consumen dos endpoints y la parte de disponibilidad implica llamadas de red.
/// </summary>
public class ModelCatalogService : IModelCatalogService
{
    private readonly IModelAvailabilityService _availability;

    public ModelCatalogService(IModelAvailabilityService availability) => _availability = availability;

    public async Task<List<ModelLifecycleResponse>> GetLifecycleAsync(
        UserDbContext db, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var available = await ResolveAvailabilityAsync(db, ct);

        return ModelCatalog.AllModels
            .Select(m => BuildLifecycle(m, today, available))
            .ToList();
    }

    public async Task<List<ModelPriceResponse>> GetPricingAsync(
        UserDbContext db, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var available = await ResolveAvailabilityAsync(db, ct);

        var result = new List<ModelPriceResponse>();

        foreach (var m in ModelCatalog.AllModels)
        {
            var rates = ResolveRates(m);

            result.Add(new ModelPriceResponse(
                m.Id,
                m.DisplayName,
                m.Provider,
                rates.Kind,
                rates.InputPerMTok,
                rates.OutputPerMTok,
                rates.ImageLow,
                rates.ImageMedium,
                rates.ImageHigh,
                rates.AuxAmount,
                rates.AuxUnit,
                rates.AuxNote,
                PrimaryModuleType(m),
                BuildLifecycle(m, today, available),
                m.Capabilities ?? [],
                m.ContextTokens,
                m.PromptChars));
        }

        return result;
    }

    /// <summary>
    /// Tarifas de un modelo del catalogo, ya resueltas segun como se facture.
    /// Los campos que no aplican a su <paramref name="Kind"/> vienen a null.
    /// </summary>
    public record ModelRates(
        string Kind,
        decimal? InputPerMTok, decimal? OutputPerMTok,
        decimal? ImageLow, decimal? ImageMedium, decimal? ImageHigh,
        decimal? AuxAmount, string? AuxUnit, string? AuxNote);

    /// <summary>
    /// Resuelve como se factura un modelo. Vive aqui y no repetido en cada llamante
    /// porque la pantalla de modelos y el servicio de deteccion de cambios tienen que
    /// leer exactamente el mismo precio: si divergieran, el escaneo detectaria cambios
    /// que la tabla no muestra.
    /// </summary>
    public static ModelRates ResolveRates(ModelCatalog.CatalogModel m)
    {
        var isText = m.Types.Contains("Text", StringComparer.OrdinalIgnoreCase);
        var isImage = m.Types.Contains("Image", StringComparer.OrdinalIgnoreCase);

        // Texto e imagen tienen tabla propia; embeddings, audio, transcripcion y
        // diseño caen en "other", donde cada uno trae su unidad de facturacion.
        // Ninguno se descarta: un modelo que no aparece aqui es indistinguible de
        // uno que no existe, y esa confusion ya costo un rato de busqueda.
        var kind = isText ? "text" : isImage ? "image" : "other";

        var rate = isText ? PricingCatalog.GetTextRate(m.Id) : null;
        var aux = kind == "other" ? PricingCatalog.GetAuxiliaryRate(m.Id) : null;

        return new ModelRates(
            kind,
            rate?.InputPerMTok,
            rate?.OutputPerMTok,
            isImage ? ImageCost(m.Id, "low") : null,
            isImage ? ImageCost(m.Id, "medium") : null,
            isImage ? ImageCost(m.Id, "high") : null,
            aux?.Amount,
            aux?.Unit,
            aux?.Note);
    }

    /// <summary>
    /// Tipo de modulo con el que se etiqueta el modelo en la pantalla de precios.
    /// Un modelo puede servir para varios (Text tambien vale de Orchestrator), pero
    /// aqui interesa el que define como se factura.
    /// </summary>
    private static string PrimaryModuleType(ModelCatalog.CatalogModel model)
    {
        string[] order = ["Text", "Image", "Embeddings", "Audio", "Transcription", "Design"];

        return order.FirstOrDefault(t => model.Types.Contains(t, StringComparer.OrdinalIgnoreCase))
               ?? model.Types.FirstOrDefault()
               ?? "Text";
    }

    private static decimal ImageCost(string modelId, string quality) =>
        PricingCatalog.EstimateImageCost(modelId, new Dictionary<string, object>
        {
            ["quality"] = quality,
            ["size"] = "1024x1024",
        });

    private static ModelLifecycleResponse BuildLifecycle(
        ModelCatalog.CatalogModel model,
        DateOnly today,
        Dictionary<string, IReadOnlySet<string>?> available)
    {
        var lifecycle = ModelLifecycle.Resolve(model.Id, today);

        bool? isAvailable = available.TryGetValue(model.Provider, out var ids) && ids is not null
            ? ids.Contains(model.Id)
            : null;

        bool? priceIsExact = model.Types.Contains("Text", StringComparer.OrdinalIgnoreCase)
            ? PricingCatalog.HasExactTextPrice(model.Id)
            : null;

        return new ModelLifecycleResponse(
            model.Id,
            model.Provider,
            lifecycle.Status.ToString().ToLowerInvariant(),
            lifecycle.ShutdownDate?.ToString("yyyy-MM-dd"),
            lifecycle.DaysUntilShutdown(today),
            lifecycle.ReplacementId,
            lifecycle.Note,
            isAvailable,
            priceIsExact);
    }

    /// <summary>
    /// Una consulta al proveedor por cada proveedor con key configurada. Se cachea en
    /// <see cref="ModelAvailabilityService"/>, asi que repetir la llamada sale gratis.
    /// </summary>
    private async Task<Dictionary<string, IReadOnlySet<string>?>> ResolveAvailabilityAsync(
        UserDbContext db, CancellationToken ct)
    {
        var keys = await db.ApiKeys
            .Where(k => k.EncryptedKey != null && k.EncryptedKey != "")
            .Select(k => new { k.ProviderType, k.EncryptedKey })
            .ToListAsync(ct);

        var byProvider = new Dictionary<string, IReadOnlySet<string>?>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in keys.GroupBy(k => k.ProviderType, StringComparer.OrdinalIgnoreCase))
            byProvider[group.Key] = await _availability.GetAvailableModelIdsAsync(
                group.Key, group.First().EncryptedKey, ct);

        return byProvider;
    }
}
