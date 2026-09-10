using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;

namespace Server.Services
{
    /// <summary>
    /// Duplicado de un modulo del catalogo. La copia no nace en blanco: se lleva el
    /// historial de versiones del prompt (para poder restaurar cualquier punto del
    /// original) y las excepciones de regla (para comportarse igual que el original
    /// desde el primer momento).
    ///
    /// No se copian los archivos de modulo: cuelgan del nodo del pipeline
    /// (ProjectModule), no del modulo del catalogo.
    /// </summary>
    public static class ModuleDuplication
    {
        /// <summary>
        /// Crea la copia de <paramref name="source"/> y la persiste. Los tres
        /// campos opcionales sobreescriben lo que traiga el original: es lo que el
        /// usuario haya cambiado en el editor antes de pulsar duplicar.
        /// </summary>
        public static async Task<AiModule> DuplicateAsync(
            UserDbContext db,
            AiModule source,
            string? name,
            string? description,
            string? configuration,
            DateTime now,
            CancellationToken ct = default)
        {
            var copy = new AiModule
            {
                Id = Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(name) ? $"{source.Name} (copia)" : name.Trim(),
                Description = description ?? source.Description,
                ProviderType = source.ProviderType,
                ModuleType = source.ModuleType,
                ModelName = source.ModelName,
                ApiKeyId = source.ApiKeyId,
                Configuration = configuration ?? source.Configuration,
                IsEnabled = source.IsEnabled,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.AiModules.Add(copy);

            // Historial: cada version conserva su fecha y su origen. Reescribirlas a
            // "ahora" pondria todas las versiones en el mismo instante y el historial
            // dejaria de contar cuando se cambio cada cosa.
            var history = await db.PromptVersions
                .Where(v => v.AiModuleId == source.Id)
                .OrderBy(v => v.CreatedAt)
                .ToListAsync(ct);

            foreach (var v in history)
            {
                db.PromptVersions.Add(new PromptVersion
                {
                    Id = Guid.NewGuid(),
                    AiModuleId = copy.Id,
                    Field = v.Field,
                    Content = v.Content,
                    Source = v.Source,
                    CreatedAt = v.CreatedAt,
                });
            }

            // Excepciones de regla: sin copiarlas, la copia recuperaria en silencio
            // reglas de las que el original estaba fuera.
            var exceptions = await db.RuleExceptions
                .Where(x => x.AiModuleId == source.Id)
                .ToListAsync(ct);

            foreach (var x in exceptions)
            {
                db.RuleExceptions.Add(new RuleException
                {
                    Id = Guid.NewGuid(),
                    RuleKey = x.RuleKey,
                    AiModuleId = copy.Id,
                    CreatedAt = now,
                });
            }

            await db.SaveChangesAsync(ct);
            return copy;
        }
    }
}
