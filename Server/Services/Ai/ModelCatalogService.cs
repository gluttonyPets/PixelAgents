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
            var isText = m.Types.Contains("Text", StringComparer.OrdinalIgnoreCase);
            var isImage = m.Types.Contains("Image", StringComparer.OrdinalIgnoreCase);

            // Un modelo puede declarar varios tipos, pero solo texto e imagen tienen
            // tarifa; el resto (embeddings, audio) no entra en esta pantalla.
            if (!isText && !isImage) continue;

            var rate = isText ? PricingCatalog.GetTextRate(m.Id) : null;

            result.Add(new ModelPriceResponse(
                m.Id,
                m.DisplayName,
                m.Provider,
                isText ? "text" : "image",
                rate?.InputPerMTok,
                rate?.OutputPerMTok,
                isImage ? ImageCost(m.Id, "low") : null,
                isImage ? ImageCost(m.Id, "medium") : null,
                isImage ? ImageCost(m.Id, "high") : null,
                BuildLifecycle(m, today, available)));
        }

        return result;
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
