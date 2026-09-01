namespace Server.Services.Ai.Handlers;

/// <summary>
/// Directorio de archivos: modulo de solo salida que emite el INDICE del
/// directorio, no los bytes de los ficheros.
///
/// Al modulo se le da un indice con las carpetas y subcarpetas, la descripcion
/// de cada fichero y la ruta accesible desde la que se puede descargar. El
/// modulo lo valida, resuelve esas rutas a URLs absolutas y las pasa al modulo
/// de destino, que decide cual necesita y se lo baja el mismo. Asi un directorio
/// grande no arrastra su peso por el pipeline: viaja el indice, no los ficheros.
///
/// Los ficheros subidos a este nodo se exponen por la URL publica del
/// directorio; los que ya viven en un repositorio externo expuesto se referencian
/// con su propia URL.
/// </summary>
public class FileDirectoryModuleHandler : IModuleHandler
{
    public string ModuleType => FileDirectoryIndex.ModuleType;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var indexJson = ctx.GetConfig(FileDirectoryIndex.IndexConfigKey);
        var baseUrl = ctx.GetConfig(FileDirectoryIndex.BaseUrlConfigKey);
        var format = ctx.GetConfig(FileDirectoryIndex.FormatConfigKey, "markdown");

        var result = FileDirectoryIndex.Resolve(
            indexJson,
            baseUrl,
            ctx.ModuleFiles.Select(f => f.FileName),
            path => FileDirectoryIndex.Absolutize(
                ctx.PublicBaseUrl,
                FileDirectoryIndex.BuildPublicPath(ctx.TenantDbName, ctx.Node.ModuleId, path)));

        if (!result.IsValid)
        {
            var detail = result.Errors.Count > 0
                ? string.Join(" | ", result.Errors)
                : "El indice no declara ningun fichero valido.";
            return ModuleResult.Failed($"Indice del directorio invalido: {detail}");
        }

        var content = FileDirectoryIndex.Render(result, format);

        await ctx.LogInfoAsync(
            $"Directorio publicado: {result.Entries.Count} fichero(s) en {result.Folders.Count} carpeta(s).");

        var output = new StepOutput
        {
            Type = "text",
            Title = ctx.Node.ProjectModule.StepName ?? ctx.Node.AiModule.Name,
            Content = content,
            Summary = $"Indice de {result.Entries.Count} fichero(s) en {result.Folders.Count} carpeta(s).",
            Metadata =
            {
                ["fileCount"] = result.Entries.Count,
                ["folderCount"] = result.Folders.Count,
                ["indexUrl"] = FileDirectoryIndex.Absolutize(
                    ctx.PublicBaseUrl,
                    FileDirectoryIndex.BuildPublicIndexPath(ctx.TenantDbName, ctx.Node.ModuleId)),
            },
        };

        if (result.BaseUrl is not null)
            output.Metadata["baseUrl"] = result.BaseUrl;

        return ModuleResult.Completed(output);
    }
}
