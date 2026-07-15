using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;

namespace Server.Services.Ai;

public sealed record SystemModuleDefinition(
    string Name,
    string Description,
    string ModuleType,
    string ModelName);

public static class SystemModuleCatalog
{
    public static readonly IReadOnlyList<SystemModuleDefinition> DefaultModules =
    [
        new(
            "Archivos",
            "Sube cualquier tipo de archivo y exponlo como recurso para el pipeline",
            "FileUpload",
            "file-upload"),
        new(
            "Texto plano",
            "Guarda texto plano y lo expone como recurso reutilizable del pipeline",
            "StaticText",
            "static-text"),
        new(
            "Checkpoint",
            "Pausa la ejecucion para revisar los datos antes de continuar",
            "Checkpoint",
            "checkpoint"),
        new(
            "Publicar",
            "Publica contenido en redes sociales (Instagram, TikTok, Pinterest, Threads)",
            "Publish",
            "publish"),
    ];

    public static bool TryGetDefinition(string? providerType, string? moduleType, out SystemModuleDefinition definition)
    {
        definition = default!;
        if (!string.Equals(providerType, "System", StringComparison.OrdinalIgnoreCase))
            return false;

        var match = DefaultModules.FirstOrDefault(d =>
            string.Equals(d.ModuleType, moduleType, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return false;

        definition = match;
        return true;
    }

    public static async Task EnsureDefaultModulesAsync(UserDbContext db, CancellationToken ct = default)
    {
        foreach (var definition in DefaultModules)
            await EnsureModuleAsync(db, definition, ct);

        await ConsolidatePublishModulesAsync(db, ct);
    }

    /// <summary>
    /// Consolida los modulos de publicacion en uno solo. Deja unicamente el
    /// modulo generico "Publicar" (ModelName "publish"), que ya contempla todas
    /// las redes (la plataforma se elige por nodo en el inspector del pipeline),
    /// y elimina los sentinelas por plataforma que versiones anteriores creaban
    /// al conectar cada red (TikTok/Pinterest/Threads/Buffer Publish).
    ///
    /// Los nodos de pipeline que apuntaban a un sentinela se reasignan al modulo
    /// generico ANTES de borrarlo (el FK ProjectModule -> AiModule es Cascade,
    /// asi que borrar sin reasignar eliminaria los nodos del usuario). La red del
    /// sentinela se conserva en la config "provider" del nodo si aun no la tenia.
    /// </summary>
    public static async Task ConsolidatePublishModulesAsync(UserDbContext db, CancellationToken ct = default)
    {
        // Sentinelas obsoletos: mismo ModuleType "Publish" pero distintos del canonico.
        var legacy = await db.AiModules
            .Where(m => m.ModuleType == "Publish" && m.ModelName != "publish")
            .ToListAsync(ct);
        if (legacy.Count == 0)
            return;

        // Garantiza el modulo canonico y toma su Id como destino de reasignacion.
        var canonical = await db.AiModules.FirstOrDefaultAsync(
            m => m.ProviderType == "System" && m.ModuleType == "Publish" && m.ModelName == "publish", ct);
        if (canonical is null)
        {
            var def = DefaultModules.First(d => d.ModuleType == "Publish");
            canonical = await EnsureModuleAsync(db, def, ct);
        }

        var legacyById = legacy.ToDictionary(m => m.Id);
        var legacyIds = legacy.Select(m => m.Id).ToList();
        var affectedNodes = await db.ProjectModules
            .Where(pm => legacyIds.Contains(pm.AiModuleId))
            .ToListAsync(ct);

        foreach (var node in affectedNodes)
        {
            var sentinel = legacyById[node.AiModuleId];
            node.AiModuleId = canonical.Id;
            node.Configuration = EnsureProviderConfig(node.Configuration, PlatformFromModelName(sentinel.ModelName));
            node.UpdatedAt = DateTime.UtcNow;
        }

        db.AiModules.RemoveRange(legacy);
        await db.SaveChangesAsync(ct);
    }

    // Traduce el ModelName del sentinela a un valor "provider" valido del inspector.
    private static string PlatformFromModelName(string? modelName) => (modelName ?? "").ToLowerInvariant() switch
    {
        "tiktok" => "tiktok",
        "pinterest" => "pinterest",
        "threads" => "threads",
        _ => "instagram",
    };

    // Fija "provider" en la config JSON del nodo solo si aun no existe, para no
    // pisar la eleccion que el usuario ya hubiese hecho en el inspector.
    private static string? EnsureProviderConfig(string? configJson, string provider)
    {
        Dictionary<string, JsonElement> map;
        try
        {
            map = string.IsNullOrWhiteSpace(configJson)
                ? new Dictionary<string, JsonElement>()
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(configJson) ?? new();
        }
        catch
        {
            map = new Dictionary<string, JsonElement>();
        }

        if (map.ContainsKey("provider"))
            return configJson; // el nodo ya define la red: respetar su valor

        var merged = new Dictionary<string, object?>();
        foreach (var (key, value) in map)
            merged[key] = value;
        merged["provider"] = provider;
        return JsonSerializer.Serialize(merged);
    }

    public static async Task<AiModule> EnsureModuleAsync(
        UserDbContext db,
        SystemModuleDefinition definition,
        CancellationToken ct = default)
    {
        var existing = await db.AiModules
            .FirstOrDefaultAsync(m =>
                m.ProviderType == "System" &&
                m.ModuleType == definition.ModuleType, ct);

        if (existing is not null)
        {
            existing.Name = definition.Name;
            existing.Description = definition.Description;
            existing.ModelName = definition.ModelName;
            existing.IsEnabled = true;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var now = DateTime.UtcNow;
        var module = new AiModule
        {
            Id = Guid.NewGuid(),
            Name = definition.Name,
            Description = definition.Description,
            ProviderType = "System",
            ModuleType = definition.ModuleType,
            ModelName = definition.ModelName,
            IsEnabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.AiModules.Add(module);
        await db.SaveChangesAsync(ct);
        return module;
    }
}
