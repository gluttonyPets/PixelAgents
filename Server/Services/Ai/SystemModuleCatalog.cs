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
    private static readonly HashSet<string> PreservedSystemModuleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "FileUpload",
        "StaticText",
        "Checkpoint",
        "Start",
    };

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

        await DeleteLegacySystemModulesAsync(db, ct);
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

    private static async Task DeleteLegacySystemModulesAsync(UserDbContext db, CancellationToken ct)
    {
        var systemModules = await db.AiModules
            .Where(m => m.ProviderType == "System")
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        var modulesToDelete = systemModules
            .Where(m => !PreservedSystemModuleTypes.Contains(m.ModuleType))
            .ToList();

        foreach (var moduleType in DefaultModules.Select(d => d.ModuleType))
        {
            var modulesOfType = systemModules
                .Where(m => string.Equals(m.ModuleType, moduleType, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.CreatedAt)
                .ToList();

            if (modulesOfType.Count <= 1)
                continue;

            var keep = modulesOfType.First();
            modulesToDelete.AddRange(modulesOfType.Where(m => m.Id != keep.Id));
        }

        modulesToDelete = modulesToDelete
            .GroupBy(m => m.Id)
            .Select(g => g.First())
            .ToList();

        if (modulesToDelete.Count == 0)
            return;

        var moduleIds = modulesToDelete.Select(m => m.Id).ToList();
        var projectModuleIds = await db.ProjectModules
            .Where(pm => moduleIds.Contains(pm.AiModuleId))
            .Select(pm => pm.Id)
            .ToListAsync(ct);

        if (projectModuleIds.Count > 0)
        {
            var oldSteps = await db.StepExecutions
                .Where(se => projectModuleIds.Contains(se.ProjectModuleId))
                .ToListAsync(ct);

            if (oldSteps.Count > 0)
                db.StepExecutions.RemoveRange(oldSteps);
        }

        db.AiModules.RemoveRange(modulesToDelete);
        await db.SaveChangesAsync(ct);
    }
}
