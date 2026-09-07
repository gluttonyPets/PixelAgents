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
///
/// Que documentos entran en cada ejecucion se elige con la carpeta configurada en
/// el nodo (<c>folder</c>): vacia publica el directorio entero, y escrita como
/// marcador de variable (<c>{{carpeta}}</c>) la decide el valor que se da al
/// lanzar cada ejecucion.
/// </summary>
public class FileDirectoryModuleHandler : IModuleHandler
{
    public string ModuleType => FileDirectoryIndex.ModuleType;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        // El indice se lee SOLO de la configuracion del nodo, no de la mezclada.
        // El modulo de catalogo es unico y comun a todos los directorios, asi que
        // un indice ahi se aplicaria a todos, con ids de ficheros subidos a otro
        // nodo: el explorador ensenaria una cosa y la ejecucion usaria otra.
        var nodeConfig = ctx.Node.ProjectModule.Configuration;
        var indexJson = FileDirectoryIndex.ReadConfig(null, nodeConfig, FileDirectoryIndex.IndexConfigKey);

        // baseUrl y formato no referencian ficheros, asi que si valen como
        // ajuste heredado del modulo de catalogo.
        var baseUrl = ctx.GetConfig(FileDirectoryIndex.BaseUrlConfigKey);
        var format = ctx.GetConfig(FileDirectoryIndex.FormatConfigKey, "markdown");

        // La carpeta se lee de la configuracion ya sustituida: puede estar escrita a
        // mano o venir del valor que la variable tome en esta ejecucion.
        var folderConfig = ctx.GetConfig(FileDirectoryIndex.FolderConfigKey);
        var unresolved = ExecutionVariables.UnresolvedKeys(folderConfig);
        if (unresolved.Count > 0)
        {
            // Sin valor no se puede adivinar el alcance: publicar el directorio entero
            // seria darle al modelo documentos que esta ejecucion no habia pedido.
            var markers = string.Join(", ", unresolved.Select(ExecutionVariables.Marker));
            return ModuleResult.Failed(
                $"La carpeta del directorio sale de {markers}, que esta ejecucion ha dejado sin valor. "
                + "Dale valor al lanzar la ejecucion o quita la carpeta de la configuracion del nodo.");
        }

        var folderSelection = FileDirectoryIndex.ParseFolderSelection(folderConfig);

        var legacyIndex = FileDirectoryIndex.ReadConfig(
            ctx.Node.AiModule.Configuration, null, FileDirectoryIndex.IndexConfigKey);
        if (legacyIndex is not null)
        {
            await ctx.LogWarningAsync(
                "El modulo de catalogo arrastra un indice antiguo que se ignora: el contenido del " +
                "directorio es de cada nodo. Si el nodo sale vacio, vuelve a montarlo en su explorador.");
        }

        var result = FileDirectoryIndex.Resolve(
            indexJson,
            baseUrl,
            ctx.ModuleFiles.Select(f => new FileDirectoryIndex.HostedFile(f.Id, f.FileName)),
            path => FileDirectoryIndex.Absolutize(
                ctx.PublicBaseUrl,
                FileDirectoryIndex.BuildPublicPath(ctx.TenantDbName, ctx.Node.ModuleId, path)),
            folderSelection);

        await ctx.LogDebugAsync(
            $"Directorio: {ctx.ModuleFiles.Count} fichero(s) subidos al nodo, "
            + $"{result.Entries.Count} entrada(s) resueltas del indice.");

        if (!result.IsValid)
        {
            var detail = result.Errors.Count > 0
                ? string.Join(" | ", result.Errors)
                : "El indice no declara ningun fichero valido.";
            return ModuleResult.Failed($"Indice del directorio invalido: {detail}");
        }

        var content = FileDirectoryIndex.Render(result, format);

        var scope = result.SelectedFolders.Count > 0
            ? $" (carpeta de esta ejecucion: {string.Join(", ", result.SelectedFolders)})"
            : "";

        await ctx.LogInfoAsync(
            $"Directorio publicado: {result.Entries.Count} fichero(s) en {result.Folders.Count} carpeta(s){scope}.");

        var output = new StepOutput
        {
            Type = "text",
            Title = ctx.Node.ProjectModule.StepName ?? ctx.Node.AiModule.Name,
            Content = content,
            Summary = $"Indice de {result.Entries.Count} fichero(s) en {result.Folders.Count} carpeta(s){scope}.",
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

        if (result.SelectedFolders.Count > 0)
            output.Metadata["folders"] = string.Join(", ", result.SelectedFolders);

        return ModuleResult.Completed(output);
    }
}
