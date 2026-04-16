namespace Server.Services.Ai.Handlers;

/// <summary>Copies attached module files to the workspace and emits them as output.</summary>
public class FileUploadModuleHandler : IModuleHandler
{
    public string ModuleType => "FileUpload";

    public Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        if (ctx.ModuleFiles.Count == 0)
        {
            var emptyOutput = new StepOutput { Type = "file", Content = "(sin archivos)" };
            return Task.FromResult(ModuleResult.Completed(emptyOutput));
        }

        var producedFiles = new List<ProducedFile>();
        var outputFiles = new List<OutputFile>();

        foreach (var f in ctx.ModuleFiles)
        {
            var sourcePath = Path.Combine(ctx.MediaRoot, f.FilePath);
            if (!File.Exists(sourcePath)) continue;

            var fileData = File.ReadAllBytes(sourcePath);
            producedFiles.Add(new ProducedFile
            {
                Data = fileData,
                FileName = f.FileName,
                ContentType = f.ContentType,
            });

            outputFiles.Add(new OutputFile
            {
                FileName = f.FileName,
                ContentType = f.ContentType,
                FileSize = f.FileSize,
            });
        }

        var output = new StepOutput
        {
            Type = "file",
            Content = $"{outputFiles.Count} archivo(s) cargados",
            Files = outputFiles,
        };

        return Task.FromResult(ModuleResult.Completed(output, files: producedFiles));
    }
}
