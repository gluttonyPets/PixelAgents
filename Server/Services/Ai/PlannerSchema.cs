using System.Text.Json;
using Server.Models;

namespace Server.Services.Ai;

/// <summary>
/// Contrato entre el planificador y el modelo: como se le pide la lista de
/// ejecuciones futuras y como se lee su respuesta.
///
/// Cuando el pipeline declara variables (<see cref="ProjectVariable"/>), cada
/// ejecucion planificada no es solo un prompt: tambien trae el valor de esas
/// variables. Se piden juntas en la misma llamada para que valor y prompt sean
/// coherentes entre si.
/// </summary>
public static class PlannerSchema
{
    /// <summary>
    /// Bloque final del prompt del planificador: que variables hay que rellenar
    /// (si las hay) y el JSON exacto que debe devolver.
    /// </summary>
    /// <param name="folderOptions">Para las variables de tipo carpeta, las carpetas que
    /// existen de verdad en la biblioteca. El planificador tiene que elegir una de ellas:
    /// una carpeta inventada tumbaria la ejecucion programada.</param>
    public static string BuildInstruction(
        int count,
        IReadOnlyList<ProjectVariable> variables,
        IReadOnlyDictionary<string, List<string>>? folderOptions = null)
    {
        // El asistente de una sola ejecucion pide el mismo contrato con un unico
        // elemento, asi que la cuenta se escribe en singular cuando toca.
        var promptsPedidos = count == 1 ? "1 prompt" : $"{count} prompts ordenados";
        var ejecucionesPedidas = count == 1 ? "1 ejecucion" : $"{count} ejecuciones ordenadas";

        if (variables.Count == 0)
        {
            return
$@"Devuelve EXCLUSIVAMENTE un JSON valido con este formato exacto, sin texto adicional, sin comentarios, sin markdown:
{{
  ""prompts"": [
    ""primer prompt"",
    ""segundo prompt""
  ]
}}
La lista debe contener exactamente {promptsPedidos}.";
        }

        // Ficha de cada variable: su nombre y para que sirve, que es lo unico que
        // el proyecto declara. El valor lo tiene que decidir el planificador.
        var varList = string.Join("\n", variables.Select(v =>
        {
            // Una variable de carpeta no se describe: se elige de una lista cerrada.
            if (ProjectVariableTypes.IsFolder(v.Type))
            {
                var options = FolderOptionsFor(v, folderOptions);
                return options.Count > 0
                    ? $@"- ""{v.Key}"": carpeta de la biblioteca. Elige EXACTAMENTE una de estas: {string.Join(", ", options.Select(Quote))}"
                    : $@"- ""{v.Key}"": carpeta de la biblioteca (ahora mismo no hay carpetas: dejala vacia)";
            }

            var description = string.IsNullOrWhiteSpace(v.Description) ? "sin descripcion" : v.Description!.Trim();
            return $@"- ""{v.Key}"": {description}";
        }));

        var varKeys = string.Join(", ", variables.Select(v => $@"""{v.Key}"""));
        var varSample = string.Join(", ", variables.Select(v => $@"""{v.Key}"": ""valor para este prompt"""));

        return
$@"Ademas del prompt, cada ejecucion fija el valor de estas variables del pipeline:
{varList}

Para CADA ejecucion tienes que dar un valor concreto y coherente con su prompt a todas
las variables ({varKeys}). Nada de valores vacios ni genericos, y no repitas el mismo
valor en varias ejecuciones salvo que el prompt lo pida.

Devuelve EXCLUSIVAMENTE un JSON valido con este formato exacto, sin texto adicional, sin comentarios, sin markdown:
{{
  ""prompts"": [
    {{
      ""prompt"": ""primer prompt"",
      ""variables"": {{ {varSample} }}
    }}
  ]
}}
La lista debe contener exactamente {ejecucionesPedidas}.";
    }

    /// <summary>
    /// Lee la lista de ejecuciones que ha devuelto el modelo. Se aceptan las dos
    /// formas: la cadena suelta (pipeline sin variables, o modelo que ignora el
    /// esquema) y el objeto con <c>prompt</c> + <c>variables</c>. De las variables
    /// solo se guardan las que el proyecto declara: lo que el modelo se invente
    /// por su cuenta se descarta.
    /// </summary>
    public static List<PlannedPromptDraft> ParsePrompts(
        string raw, int expected, IReadOnlyList<ProjectVariable> variables,
        IReadOnlyDictionary<string, List<string>>? folderOptions = null)
    {
        var json = ExtractJsonObject(raw);
        if (json is null) return [];

        // Clave escrita por el modelo -> clave tal y como la declara el proyecto:
        // si el modelo cambia mayusculas, el valor se guarda igualmente con el
        // nombre canonico y la UI lo encuentra.
        var declared = variables
            .Where(v => ExecutionVariables.IsValidKey(v.Key))
            .GroupBy(v => v.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Key.Trim(), StringComparer.OrdinalIgnoreCase);

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("prompts", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<PlannedPromptDraft>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        list.Add(new PlannedPromptDraft(text.Trim(), ExecutionVariables.NewMap()));
                    continue;
                }

                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("prompt", out var promptProp)
                    || promptProp.ValueKind != JsonValueKind.String) continue;

                var content = promptProp.GetString();
                if (string.IsNullOrWhiteSpace(content)) continue;

                list.Add(new PlannedPromptDraft(
                    content.Trim(),
                    ReadVariables(item, declared, variables, folderOptions)));
            }

            if (list.Count > expected) list = list.Take(expected).ToList();
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Valores propuestos para una ejecucion, filtrados a las variables declaradas.
    /// En las de tipo carpeta se descarta lo que no sea una carpeta real: es mejor que
    /// la fila de la cola se vea sin carpeta —y se elija en su desplegable— a que la
    /// ejecucion salga con una carpeta inventada y falle al llegar a la biblioteca.
    /// </summary>
    private static Dictionary<string, string> ReadVariables(
        JsonElement item,
        IReadOnlyDictionary<string, string> declared,
        IReadOnlyList<ProjectVariable> variables,
        IReadOnlyDictionary<string, List<string>>? folderOptions)
    {
        var values = ExecutionVariables.NewMap();
        if (declared.Count == 0) return values;
        if (!item.TryGetProperty("variables", out var varsProp) || varsProp.ValueKind != JsonValueKind.Object)
            return values;

        foreach (var prop in varsProp.EnumerateObject())
        {
            if (!declared.TryGetValue(prop.Name.Trim(), out var canonicalKey)) continue;

            var value = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.Value.GetRawText(),
                _ => null,
            };

            if (string.IsNullOrWhiteSpace(value)) continue;

            var trimmed = value!.Trim();
            var definition = variables.FirstOrDefault(v =>
                string.Equals(v.Key.Trim(), canonicalKey, StringComparison.OrdinalIgnoreCase));

            if (definition is not null && ProjectVariableTypes.IsFolder(definition.Type))
            {
                var options = FolderOptionsFor(definition, folderOptions);
                if (options.Count > 0
                    && !options.Any(o => string.Equals(o, trimmed, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }

            values[canonicalKey] = trimmed;
        }

        return values;
    }

    /// <summary>Carpetas que puede elegir una variable, tal y como se le ofrecen al modelo.</summary>
    private static List<string> FolderOptionsFor(
        ProjectVariable variable,
        IReadOnlyDictionary<string, List<string>>? folderOptions)
    {
        if (folderOptions is null) return [];
        return folderOptions.TryGetValue(variable.Key.Trim(), out var options) ? options : [];
    }

    private static string Quote(string value) => '"' + value + '"';

    /// <summary>Recorta el JSON del texto del modelo (puede venir con vallas markdown).</summary>
    private static string? ExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            var firstNewline = cleaned.IndexOf('\n');
            if (firstNewline >= 0) cleaned = cleaned[(firstNewline + 1)..];
            var fenceEnd = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0) cleaned = cleaned[..fenceEnd];
            cleaned = cleaned.Trim();
        }

        var start = cleaned.IndexOf('{');
        var end = cleaned.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return cleaned.Substring(start, end - start + 1);
    }
}
