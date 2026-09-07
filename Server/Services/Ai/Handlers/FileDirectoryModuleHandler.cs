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
/// Que documentos entran en cada ejecucion se decide en dos sitios:
///
///   1. La carpeta escrita en el nodo (<c>folder</c>), a mano o como marcador de
///      variable (<c>{{carpeta}}</c>). Lo explicito manda.
///   2. Si el nodo no dice nada, las variables de tipo carpeta del pipeline que
///      apunten a este directorio (o a ninguno): la biblioteca entrega solo la
///      carpeta que la variable trae, sin tener que cablear el marcador aqui.
///
/// Sin lo uno ni lo otro se publica el directorio entero, como siempre.
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

        // Sin carpeta escrita en el nodo mandan las variables de tipo carpeta: si la
        // ejecucion (o la planificacion) eligio una, la biblioteca entrega solo eso.
        var fromVariables = folderSelection.Count == 0;
        if (fromVariables)
        {
            folderSelection = VariableFolders.ForModule(
                ctx.VariableDefinitions, ctx.Variables, ctx.Node.ModuleId);

            // Hay variable de carpeta pero esta ejecucion no eligio ninguna: publicar
            // la biblioteca entera seria justo lo contrario de lo que pide el pipeline.
            var applicable = VariableFolders.Applicable(ctx.VariableDefinitions, ctx.Node.ModuleId);
            if (applicable.Count > 0 && folderSelection.Count == 0)
            {
                var markers = string.Join(", ", applicable.Select(v => ExecutionVariables.Marker(v.Key)));
                return ModuleResult.Failed(
                    $"Este directorio lo recorta {markers}, y esta ejecucion no ha elegido carpeta. "
                    + "Elige una al lanzar la ejecucion o en la planificacion, o quita la variable del pipeline.");
            }
        }

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

        if (fromVariables && result.SelectedFolders.Count > 0)
        {
            var keys = VariableFolders.KeysForModule(
                ctx.VariableDefinitions, ctx.Variables, ctx.Node.ModuleId);
            // Deja claro de donde sale el recorte: el nodo no lo tiene escrito.
            await ctx.LogInfoAsync(
                $"Carpeta tomada de {string.Join(", ", keys.Select(ExecutionVariables.Marker))}: "
                + string.Join(", ", result.SelectedFolders));
        }

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
