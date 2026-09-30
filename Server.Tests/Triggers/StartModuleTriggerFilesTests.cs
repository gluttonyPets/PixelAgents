using Server.Models;
using Server.Services.Ai;
using Server.Services.Ai.Handlers;
using Xunit;

namespace Server.Tests.Triggers;

/// <summary>
/// El modulo de entrada emite, ademas del texto, los archivos que trae el trigger
/// (el archivo nuevo de Drive), para que los modulos siguientes los reciban.
/// </summary>
public class StartModuleTriggerFilesTests
{
    [Fact]
    public async Task SinArchivosEmiteSoloElTexto()
    {
        var result = await new StartModuleHandler().ExecuteAsync(CreateContext("hola", []));

        Assert.Equal("hola", result.Output!.Content);
        Assert.Empty(result.Output.Files);
        Assert.Empty(result.ProducedFiles);
    }

    [Fact]
    public async Task EmiteLosArchivosDelTrigger()
    {
        var file = new ProducedFile { Data = [1, 2, 3], FileName = "factura.pdf", ContentType = "application/pdf" };

        var result = await new StartModuleHandler().ExecuteAsync(CreateContext("Nuevo archivo", [file]));

        Assert.Equal("Nuevo archivo", result.Output!.Content);
        var output = Assert.Single(result.Output.Files);
        Assert.Equal("factura.pdf", output.FileName);
        Assert.Equal("application/pdf", output.ContentType);
        Assert.Equal(3, output.FileSize);
        Assert.Same(file, Assert.Single(result.ProducedFiles));
    }

    private static ModuleExecutionContext CreateContext(string userInput, IReadOnlyList<ProducedFile> files)
    {
        var projectModule = new ProjectModule
        {
            Id = Guid.NewGuid(),
            IsActive = true,
            AiModule = new AiModule
            {
                Id = Guid.NewGuid(), Name = "Inicio", ModuleType = "Start", ProviderType = "System", ModelName = "start",
            },
        };

        return new ModuleExecutionContext
        {
            Node = new ModuleNode(projectModule),
            Graph = new ExecutionGraph { UserInput = userInput, InputFiles = files },
            Execution = new ProjectExecution { Id = Guid.NewGuid() },
            Project = new Project { Id = Guid.NewGuid(), Name = "test" },
            TenantDbName = "tenant_test",
            WorkspacePath = "/tmp",
            MediaRoot = "/tmp",
            PublicBaseUrl = "https://app.ejemplo.com",
            Config = new Dictionary<string, object>(),
            ModuleFiles = [],
        };
    }
}
