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
            "Carga de archivos",
            "Sube cualquier tipo de archivo y exponlo como recurso para el pipeline",
            "FileUpload",
            "file-upload"),
        new(
            "Constructor de escenas",
            "Construye escenas estructuradas para plantillas y composiciones",
            "Scene",
            "scene-builder"),
        new(
            "Texto estatico",
            "Emite texto fijo como recurso reutilizable del pipeline",
            "StaticText",
            "static-text"),
        new(
            "Checkpoint",
            "Pausa la ejecucion para revisar los datos antes de continuar",
            "Checkpoint",
            "checkpoint"),
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
            return existing;

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
