using Server.Models;

namespace Server.Services.Ai;

/// <summary>
/// Carpetas que las variables de tipo carpeta fijan para un nodo Directorio de
/// archivos.
///
/// Es la union de las dos piezas: la variable dice **que parte de la biblioteca**
/// se usa (elegida al declararla y cambiable en cada ejecucion), y el nodo la
/// aplica sin que haya que escribir el marcador en su configuracion. Un nodo que
/// si trae carpeta escrita manda sobre esto: lo explicito gana.
/// </summary>
public static class VariableFolders
{
    /// <summary>
    /// Carpetas que aplican a un nodo Directorio en esta ejecucion.
    ///
    /// Solo cuentan las variables de tipo carpeta que apuntan a ese nodo o que no
    /// apuntan a ninguno (valen para todas las bibliotecas del pipeline): una
    /// variable atada a otro directorio no puede recortar este, porque su carpeta
    /// no tiene por que existir aqui.
    /// </summary>
    public static List<string> ForModule(
        IEnumerable<ProjectVariable>? definitions,
        IReadOnlyDictionary<string, string>? values,
        Guid moduleId)
    {
        if (definitions is null) return [];

        var folders = new List<string>();

        foreach (var def in definitions)
        {
            if (!ProjectVariableTypes.IsFolder(def.Type)) continue;
            if (def.SourceModuleId is { } source && source != moduleId) continue;
            if (!ExecutionVariables.IsValidKey(def.Key)) continue;

            // El valor de la ejecucion ya cae a la carpeta elegida al declarar la
            // variable (ExecutionVariables.Resolve), asi que aqui basta con leerlo.
            if (values is null || !values.TryGetValue(def.Key.Trim(), out var value)) continue;

            folders.AddRange(FileDirectoryIndex.ParseFolderSelection(value));
        }

        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Claves de las variables de carpeta que han aportado alcance, para el log.</summary>
    public static List<string> KeysForModule(
        IEnumerable<ProjectVariable>? definitions,
        IReadOnlyDictionary<string, string>? values,
        Guid moduleId)
    {
        if (definitions is null) return [];

        return definitions
            .Where(d => ProjectVariableTypes.IsFolder(d.Type))
            .Where(d => d.SourceModuleId is not { } source || source == moduleId)
            .Where(d => ExecutionVariables.IsValidKey(d.Key))
            .Where(d => values is not null
                && values.TryGetValue(d.Key.Trim(), out var v)
                && FileDirectoryIndex.ParseFolderSelection(v).Count > 0)
            .Select(d => d.Key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
