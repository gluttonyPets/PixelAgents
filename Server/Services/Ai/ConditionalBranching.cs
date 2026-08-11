using System.Text.Json;

namespace Server.Services.Ai;

/// <summary>
/// Constantes y utilidades compartidas del modulo Condicional: nombres de
/// puertos, clave de metadatos y traduccion de "condicion cumplida" a la lista
/// de puertos de salida que quedan bloqueados (la rama que no se ejecuta).
///
/// Vive fuera del handler porque el grafo tambien lo necesita al reconstruir
/// una ejecucion pausada o reintentada: en esos casos el nodo ya esta completo
/// y la rama viva se deduce del <see cref="StepOutput.Metadata"/> persistido.
/// </summary>
public static class ConditionalBranching
{
    public const string ModuleType = "Conditional";

    /// <summary>Salida que se activa cuando la condicion se cumple.</summary>
    public const string TruePort = "output_true";

    /// <summary>Salida que se activa cuando la condicion NO se cumple.</summary>
    public const string FalsePort = "output_false";

    /// <summary>Clave en <see cref="StepOutput.Metadata"/> con el resultado.</summary>
    public const string MetadataKey = "conditionMet";

    /// <summary>Puertos que no deben propagar datos segun el resultado.</summary>
    public static IReadOnlyList<string> BlockedPortsFor(bool conditionMet) =>
        conditionMet ? [FalsePort] : [TruePort];

    /// <summary>
    /// Lee el resultado de la condicion de la salida persistida del nodo.
    /// Devuelve null si la salida no viene de un modulo condicional o no
    /// contiene el metadato (ejecuciones antiguas).
    /// </summary>
    public static bool? ReadConditionMet(StepOutput? output)
    {
        if (output is null) return null;
        if (!output.Metadata.TryGetValue(MetadataKey, out var raw)) return null;

        return raw switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var parsed) ? parsed : null,
            JsonElement je => je.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(je.GetString(), out var fromString) ? fromString : null,
                _ => null,
            },
            _ => null,
        };
    }
}
