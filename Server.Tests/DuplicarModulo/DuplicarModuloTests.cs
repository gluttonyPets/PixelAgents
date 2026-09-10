using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;
using Server.Services;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.DuplicarModulo;

/// <summary>
/// Duplicar un modulo tiene que dar un modulo equivalente, no uno en blanco con el
/// mismo texto: la copia hereda el historial de versiones del prompt (con sus fechas,
/// que son las que cuentan cuando se cambio cada cosa) y las excepciones de regla.
/// </summary>
public class DuplicarModuloTests
{
    private static UserDbContext NuevaBd() =>
        new(new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase($"dup-{Guid.NewGuid()}")
            .Options);

    private static AiModule ModuloBase() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Generar blog",
        Description = "Escribe el articulo",
        ProviderType = "OpenAI",
        ModuleType = "Text",
        ModelName = "gpt-5.2",
        ApiKeyId = Guid.NewGuid(),
        Configuration = """{"systemPrompt":"prompt v3"}""",
        IsEnabled = true,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
    };

    private static PromptVersion Version(Guid moduleId, string contenido, string origen, DateTime fecha) => new()
    {
        Id = Guid.NewGuid(),
        AiModuleId = moduleId,
        Field = "systemPrompt",
        Content = contenido,
        Source = origen,
        CreatedAt = fecha,
    };

    [Fact]
    public async Task LaCopiaHeredaElHistorialConSusFechasYOrigenes()
    {
        await using var db = NuevaBd();
        var source = ModuloBase();
        var v1 = new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);
        var v2 = new DateTime(2026, 2, 5, 10, 0, 0, DateTimeKind.Utc);
        db.AiModules.Add(source);
        db.PromptVersions.AddRange(
            Version(source.Id, "prompt v1", "baseline", v1),
            Version(source.Id, "prompt v3", "edit", v2));
        await db.SaveChangesAsync();

        var copy = await ModuleDuplication.DuplicateAsync(db, source, null, null, null, DateTime.UtcNow);

        var historial = await db.PromptVersions
            .Where(v => v.AiModuleId == copy.Id).OrderBy(v => v.CreatedAt).ToListAsync();

        Assert.Equal(2, historial.Count);
        Assert.Equal(["prompt v1", "prompt v3"], historial.Select(v => v.Content));
        Assert.Equal(["baseline", "edit"], historial.Select(v => v.Source));
        Assert.Equal([v1, v2], historial.Select(v => v.CreatedAt));
    }

    [Fact]
    public async Task ElHistorialDelOriginalNoSeToca()
    {
        await using var db = NuevaBd();
        var source = ModuloBase();
        db.AiModules.Add(source);
        db.PromptVersions.Add(Version(source.Id, "prompt v1", "edit", DateTime.UtcNow));
        await db.SaveChangesAsync();

        var copy = await ModuleDuplication.DuplicateAsync(db, source, null, null, null, DateTime.UtcNow);

        Assert.Single(await db.PromptVersions.Where(v => v.AiModuleId == source.Id).ToListAsync());
        Assert.NotEqual(source.Id, copy.Id);
    }

    [Fact]
    public async Task SinHistorial_LaCopiaTampocoLoTiene()
    {
        await using var db = NuevaBd();
        var source = ModuloBase();
        db.AiModules.Add(source);
        await db.SaveChangesAsync();

        var copy = await ModuleDuplication.DuplicateAsync(db, source, null, null, null, DateTime.UtcNow);

        Assert.Empty(await db.PromptVersions.Where(v => v.AiModuleId == copy.Id).ToListAsync());
    }

    [Fact]
    public async Task LaCopiaHeredaLasExcepcionesDeRegla()
    {
        await using var db = NuevaBd();
        var source = ModuloBase();
        var reglaPropia = BuiltInRules.TenantKey(Guid.NewGuid());
        db.AiModules.Add(source);
        db.RuleExceptions.AddRange(
            new RuleException { Id = Guid.NewGuid(), RuleKey = BuiltInRules.FormatKey, AiModuleId = source.Id },
            new RuleException { Id = Guid.NewGuid(), RuleKey = reglaPropia, AiModuleId = source.Id });
        await db.SaveChangesAsync();

        var copy = await ModuleDuplication.DuplicateAsync(db, source, null, null, null, DateTime.UtcNow);

        var claves = await db.RuleExceptions
            .Where(x => x.AiModuleId == copy.Id).Select(x => x.RuleKey).ToListAsync();

        Assert.Equal(2, claves.Count);
        Assert.Contains(BuiltInRules.FormatKey, claves);
        Assert.Contains(reglaPropia, claves);
    }

    [Fact]
    public async Task SinNombre_LaCopiaSeLlamaIgualConSufijo()
    {
        await using var db = NuevaBd();
        var source = ModuloBase();
        db.AiModules.Add(source);
        await db.SaveChangesAsync();

        var copy = await ModuleDuplication.DuplicateAsync(db, source, "   ", null, null, DateTime.UtcNow);

        Assert.Equal("Generar blog (copia)", copy.Name);
    }

    [Fact]
    public async Task LoQueSeCambioEnElEditorGanaAlOriginal()
    {
        await using var db = NuevaBd();
        var source = ModuloBase();
        db.AiModules.Add(source);
        await db.SaveChangesAsync();

        var copy = await ModuleDuplication.DuplicateAsync(
            db, source, "Generar ficha", "otra cosa", """{"systemPrompt":"prompt v4"}""", DateTime.UtcNow);

        Assert.Equal("Generar ficha", copy.Name);
        Assert.Equal("otra cosa", copy.Description);
        Assert.Contains("prompt v4", copy.Configuration);
        // Lo que no se toca sigue siendo el del original.
        Assert.Equal(source.ProviderType, copy.ProviderType);
        Assert.Equal(source.ModuleType, copy.ModuleType);
        Assert.Equal(source.ModelName, copy.ModelName);
        Assert.Equal(source.ApiKeyId, copy.ApiKeyId);
    }

    [Fact]
    public async Task LaCopiaEsIndependiente_BorrarElOriginalNoSeLaLleva()
    {
        await using var db = NuevaBd();
        var source = ModuloBase();
        db.AiModules.Add(source);
        db.PromptVersions.Add(Version(source.Id, "prompt v1", "edit", DateTime.UtcNow));
        await db.SaveChangesAsync();

        var copy = await ModuleDuplication.DuplicateAsync(db, source, null, null, null, DateTime.UtcNow);

        db.AiModules.Remove(source);
        db.PromptVersions.RemoveRange(db.PromptVersions.Where(v => v.AiModuleId == source.Id));
        await db.SaveChangesAsync();

        Assert.NotNull(await db.AiModules.FindAsync(copy.Id));
        Assert.Single(await db.PromptVersions.Where(v => v.AiModuleId == copy.Id).ToListAsync());
    }
}
