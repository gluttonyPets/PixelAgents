namespace Server.Services.Ai.Handlers;

/// <summary>
/// The Start module has no inputs and emits the user's prompt as output.
/// Every pipeline must have exactly one Start module as the entry point.
/// En un pipeline de tipo Trigger emite ademas los archivos que trae el evento
/// (p. ej. el archivo nuevo de Drive), para que los modulos siguientes los reciban.
/// </summary>
public class StartModuleHandler : IModuleHandler
{
    public string ModuleType => "Start";

    public Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var userInput = ctx.Graph.UserInput ?? "";
        var inputFiles = ctx.Graph.InputFiles;

        var output = new StepOutput
        {
            Type = "text",
            Content = userInput,
            Files = inputFiles.Select(f => new OutputFile
            {
                FileName = f.FileName,
                ContentType = f.ContentType,
                FileSize = f.Data.LongLength,
            }).ToList(),
        };

        return Task.FromResult(ModuleResult.Completed(output, files: inputFiles.ToList()));
    }
}
