using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;

namespace Server.Services
{
    /// <summary>
    /// Seeds the built-in utility modules (ProviderType == "System") that every tenant needs.
    /// Users never create these from the Modules page — they are always available from
    /// "Añadir módulo" in projects.
    /// </summary>
    public static class SystemModuleSeeder
    {
        public record SeedDef(string Name, string Description, string ModuleType, string ModelName);

        public static readonly SeedDef[] Utilities =
        [
            new("Inicio",
                "Punto de entrada del pipeline.",
                "Start", "start"),
            new("Checkpoint",
                "Pausa la ejecucion para revisar los datos antes de continuar.",
                "Checkpoint", "checkpoint"),
            new("WhatsApp Interaction",
                "Pausa el pipeline y envia un mensaje a WhatsApp para obtener feedback del usuario.",
                "Interaction", "whatsapp"),
            new("Telegram Interaction",
                "Pausa el pipeline y envia un mensaje a Telegram para obtener feedback del usuario.",
                "Interaction", "telegram"),
            new("Buffer Publish",
                "Publica contenido en redes sociales (Instagram, Twitter, etc.) via Buffer.",
                "Publish", "instagram"),
            new("TikTok Publish",
                "Publica contenido en TikTok (videos e imagenes) via Buffer.",
                "Publish", "tiktok"),
        ];

        public static readonly HashSet<(string ModuleType, string ModelName)> SingletonKeys =
            Utilities.Select(u => (u.ModuleType, u.ModelName)).ToHashSet();

        public static bool IsSingletonUtility(string providerType, string moduleType, string modelName)
            => providerType == "System" && SingletonKeys.Contains((moduleType, modelName));

        public static async Task EnsureSeededAsync(UserDbContext db)
        {
            var existing = await db.AiModules
                .Where(m => m.ProviderType == "System")
                .Select(m => new { m.ModuleType, m.ModelName })
                .ToListAsync();
            var existingSet = existing.Select(x => (x.ModuleType, x.ModelName)).ToHashSet();

            var now = DateTime.UtcNow;
            var added = false;
            foreach (var def in Utilities)
            {
                if (existingSet.Contains((def.ModuleType, def.ModelName))) continue;
                db.AiModules.Add(new AiModule
                {
                    Id = Guid.NewGuid(),
                    Name = def.Name,
                    Description = def.Description,
                    ProviderType = "System",
                    ModuleType = def.ModuleType,
                    ModelName = def.ModelName,
                    IsEnabled = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                added = true;
            }

            if (added) await db.SaveChangesAsync();
        }
    }
}
