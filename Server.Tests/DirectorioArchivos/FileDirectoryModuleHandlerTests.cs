using Server.Models;
using Server.Services.Ai;
using Server.Services.Ai.Handlers;
using Xunit;

namespace Server.Tests.DirectorioArchivos;

/// <summary>
/// Comportamiento del modulo Directorio: solo salida, emite el indice y no los
/// ficheros, y falla en vez de emitir un indice incompleto. Lo que viaja por el
/// pipeline es la lista de rutas accesibles, para que el modulo de destino baje
/// unicamente lo que necesite.
/// </summary>
public class FileDirectoryModuleHandlerTests
{
    [Fact]
    public async Task EmiteElIndiceConLaUrlDeCadaFichero()
    {
        var ctx = CreateContext("""
        {
          "baseUrl": "https://cdn.ejemplo.com/marca",
          "files": [
            { "path": "logos/logo.svg", "description": "Logo principal" },
            { "path": "fotos/frontal.jpg", "description": "Foto frontal del producto" }
          ]
        }
        """);

        var result = await new FileDirectoryModuleHandler().ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Contains("Logo principal", result.Output!.Content);
        Assert.Contains("https://cdn.ejemplo.com/marca/logos/logo.svg", result.Output.Content);
        Assert.Contains("https://cdn.ejemplo.com/marca/fotos/frontal.jpg", result.Output.Content);
    }

    [Fact]
    public async Task NoArrastraFicherosPorElPipeline()
    {
        var ctx = CreateContext("""
        {
          "baseUrl": "https://cdn.ejemplo.com",
          "files": [ { "path": "a.pdf", "description": "un pdf" } ]
        }
        """);

        var result = await new FileDirectoryModuleHandler().ExecuteAsync(ctx);

        // Lo que sale es el indice, no los bytes: ni ficheros producidos ni
        // adjuntos en la salida.
        Assert.Empty(result.ProducedFiles);
        Assert.Empty(result.Output!.Files);
        Assert.Equal("text", result.Output.Type);
    }

    [Fact]
    public async Task SinIndice_ElModuloFalla()
    {
        var ctx = CreateContext(indexJson: null);

        var result = await new FileDirectoryModuleHandler().ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("indice", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConUnFicheroSinDescripcion_FallaEnVezDePublicarloAMedias()
    {
        var ctx = CreateContext("""
        {
          "baseUrl": "https://cdn.ejemplo.com",
          "files": [
            { "path": "a.pdf", "description": "explicado" },
            { "path": "b.pdf" }
          ]
        }
        """);

        var result = await new FileDirectoryModuleHandler().ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("descripcion", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LosFicherosSubidosAlNodoSeExponenEnLaUrlPublicaDelServidor()
    {
        var ctx = CreateContext(
            """{ "files": [ { "path": "manuales/uso.pdf", "description": "Manual de uso" } ] }""",
            moduleFiles: ["uso.pdf"]);

        var result = await new FileDirectoryModuleHandler().ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Contains(
            $"https://app.ejemplo.com/api/public/directory/tenant_test/{ctx.Node.ModuleId}/manuales/uso.pdf",
            result.Output!.Content);
    }

    [Fact]
    public async Task LaSalidaLlevaElRecuentoYLaUrlDelIndice()
    {
        var ctx = CreateContext("""
        {
          "baseUrl": "https://cdn.ejemplo.com",
          "files": [
            { "path": "logos/a.svg", "description": "a" },
            { "path": "fotos/b.jpg", "description": "b" }
          ]
        }
        """);

        var result = await new FileDirectoryModuleHandler().ExecuteAsync(ctx);

        Assert.Equal(2, result.Output!.Metadata["fileCount"]);
        Assert.Equal(2, result.Output.Metadata["folderCount"]);
        Assert.Equal(
            $"https://app.ejemplo.com/api/public/directory/tenant_test/{ctx.Node.ModuleId}",
            result.Output.Metadata["indexUrl"]);
    }

    [Fact]
    public async Task EnFormatoJson_LaSalidaEsJsonParseable()
    {
        var ctx = CreateContext(
            """
            {
              "baseUrl": "https://cdn.ejemplo.com",
              "files": [ { "path": "a.txt", "description": "primero" } ]
            }
            """,
            format: "json");

        var result = await new FileDirectoryModuleHandler().ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        using var doc = System.Text.Json.JsonDocument.Parse(result.Output!.Content!);
        Assert.Equal(1, doc.RootElement.GetProperty("fileCount").GetInt32());
    }

    // ── Helpers ──

    private static ModuleExecutionContext CreateContext(
        string? indexJson,
        string? format = null,
        IEnumerable<string>? moduleFiles = null)
    {
        var aiModule = new AiModule
        {
            Id = Guid.NewGuid(),
            Name = "Directorio de archivos",
            ModuleType = FileDirectoryIndex.ModuleType,
            ProviderType = "System",
            ModelName = "file-directory",
        };
        var projectModule = new ProjectModule { Id = Guid.NewGuid(), IsActive = true, AiModule = aiModule };

        var config = new Dictionary<string, object>();
        if (indexJson is not null) config[FileDirectoryIndex.IndexConfigKey] = indexJson;
        if (format is not null) config[FileDirectoryIndex.FormatConfigKey] = format;

        return new ModuleExecutionContext
        {
            Node = new ModuleNode(projectModule),
            Graph = new ExecutionGraph(),
            Execution = new ProjectExecution { Id = Guid.NewGuid() },
            Project = new Project { Id = Guid.NewGuid(), Name = "test" },
            TenantDbName = "tenant_test",
            WorkspacePath = "/tmp",
            MediaRoot = "/tmp",
            PublicBaseUrl = "https://app.ejemplo.com",
            Config = config,
            ModuleFiles = (moduleFiles ?? [])
                .Select(name => new ModuleFileInfo
                {
                    Id = Guid.NewGuid(),
                    FileName = name,
                    ContentType = "application/octet-stream",
                    FilePath = $"tenant_test/module-files/{name}",
                })
                .ToList(),
        };
    }
}
