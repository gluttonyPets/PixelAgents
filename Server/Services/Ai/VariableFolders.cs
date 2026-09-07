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
    /// Variables de carpeta que mandan sobre un nodo Directorio: las que apuntan a ese
    /// nodo y las que no apuntan a ninguno (valen para todas las bibliotecas del
    /// pipeline). Una variable atada a otro directorio no puede recortar este, porque
    /// su carpeta no tiene por que existir aqui.
    /// </summary>
    public static List<ProjectVariable> Applicable(
        IEnumerable<ProjectVariable>? definitions,
        Guid moduleId)
    {
        if (definitions is null) return [];

        return definitions
            .Where(d => ProjectVariableTypes.IsFolder(d.Type))
            .Where(d => d.SourceModuleId is not { } source || source == moduleId)
            .Where(d => ExecutionVariables.IsValidKey(d.Key))
            .ToList();
    }

    /// <summary>Carpetas que esas variables traen en esta ejecucion.</summary>
    public static List<string> ForModule(
        IEnumerable<ProjectVariable>? definitions,
        IReadOnlyDictionary<string, string>? values,
        Guid moduleId)
    {
        var folders = new List<string>();

        foreach (var def in Applicable(definitions, moduleId))
        {
            if (values is null || !values.TryGetValue(def.Key.Trim(), out var value)) continue;
            folders.AddRange(FileDirectoryIndex.ParseFolderSelection(value));
        }

        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Claves de las variables de carpeta que han aportado alcance, para el log.</summary>
    public static List<string> KeysForModule(
        IEnumerable<ProjectVariable>? definitions,
        IReadOnlyDictionary<string, string>? values,
        Guid moduleId) =>
        Applicable(definitions, moduleId)
            .Where(d => values is not null
                && values.TryGetValue(d.Key.Trim(), out var v)
                && FileDirectoryIndex.ParseFolderSelection(v).Count > 0)
            .Select(d => d.Key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
