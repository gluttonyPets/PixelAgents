using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Server.Models;

namespace Server.Services.Ai;

/// <summary>
/// Variables de ejecucion: valores que el pipeline declara una vez (por ejemplo
/// "tematica" y "keyword") y que se fijan al lanzar cada ejecucion.
///
/// Se aplican de dos formas complementarias:
///   1. <b>Sustitucion</b>: cualquier <c>{{clave}}</c> del prompt del usuario o de
///      la configuracion de un modulo se reemplaza por el valor de la ejecucion.
///   2. <b>Inyeccion</b>: los valores viajan ademas como bloque etiquetado en el
///      system prompt de todas las llamadas a IA, para que un modulo pueda usarlos
///      aunque su prompt no lleve el marcador.
///
/// El valor es siempre de la ejecucion: una variable no tiene valor fijo (si lo
/// tuviera, seria parte del prompt y no una variable). Una variable que se quede
/// sin valor no se sustituye: el marcador se queda visible y el executor avisa en
/// el log, que es preferible a mandar al modelo un hueco vacio sin dejar rastro.
/// </summary>
public static class ExecutionVariables
{
    public const string BlockHeader = "=== VARIABLES DE LA EJECUCION ===";

    private const string BlockSubtitle =
        "(Valores fijados al lanzar esta ejecucion. Son los mismos para todos los modulos del pipeline: " +
        "usalos tal cual, no los cambies ni los sustituyas por otros.)";

    /// <summary>Longitud maxima de una clave. Suficiente para nombres legibles y
    /// evita que un marcador gigante pase por variable.</summary>
    public const int MaxKeyLength = 50;

    private static readonly Regex PlaceholderPattern =
        new(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);

    private static readonly Regex KeyPattern =
        new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>Diccionario vacio reutilizable (mismo comparador que el resto).</summary>
    public static IReadOnlyDictionary<string, string> None { get; } = NewMap();

    public static Dictionary<string, string> NewMap() => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Marcador que hay que escribir en el prompt para usar la variable.</summary>
    public static string Marker(string key) => "{{" + (key ?? "").Trim() + "}}";

    /// <summary>Claves validas: letras, digitos y guion bajo, sin empezar por digito.</summary>
    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var trimmed = key.Trim();
        return trimmed.Length <= MaxKeyLength && KeyPattern.IsMatch(trimmed);
    }

    // ── Serializacion (columna VariablesJson) ──

    public static Dictionary<string, string> Parse(string? json)
    {
        var map = NewMap();
        if (string.IsNullOrWhiteSpace(json)) return map;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!IsValidKey(prop.Name)) continue;
                var value = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.Null => "",
                    JsonValueKind.Undefined => "",
                    _ => prop.Value.GetRawText(),
                };
                map[prop.Name.Trim()] = value;
            }
        }
        catch (JsonException)
        {
            // Un JSON corrupto no puede tumbar una ejecucion: se corre sin variables.
        }

        return map;
    }

    /// <summary>Serializa los valores; null cuando no hay ninguno (columna vacia).</summary>
    public static string? Serialize(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0) return null;
        return JsonSerializer.Serialize(
            values.ToDictionary(kv => kv.Key, kv => kv.Value ?? ""),
            AiJson.Compact);
    }

    // ── Resolucion ──

    /// <summary>
    /// Valores efectivos de la ejecucion: para cada variable declarada en el
    /// proyecto, lo que aporte esta ejecucion. Las claves que la ejecucion trae
    /// pero el proyecto ya no declara se descartan (variable borrada despues de
    /// programarla), y las que se quedan sin valor no se sustituyen.
    ///
    /// Unica excepcion: una variable de tipo carpeta cae a la carpeta elegida al
    /// declararla. No es un "valor fijo" encubierto —no es contenido que entre en
    /// ningun prompt—, es que parte de la biblioteca viaja mientras la ejecucion no
    /// elija otra cosa.
    /// </summary>
    public static Dictionary<string, string> Resolve(
        IEnumerable<ProjectVariable>? definitions,
        IReadOnlyDictionary<string, string>? overrides)
    {
        var resolved = NewMap();
        if (definitions is null) return resolved;

        foreach (var def in definitions)
        {
            if (!IsValidKey(def.Key)) continue;
            var key = def.Key.Trim();

            var value = overrides is not null && overrides.TryGetValue(key, out var fromRun)
                ? fromRun
                : null;

            if (string.IsNullOrWhiteSpace(value) && ProjectVariableTypes.IsFolder(def.Type))
                value = def.FolderPath;

            if (string.IsNullOrWhiteSpace(value)) continue;   // sin valor: no se sustituye

            resolved[key] = value.Trim();
        }

        return resolved;
    }

    /// <summary>
    /// Superpone varias capas de valores; la ultima que traiga una clave gana.
    /// Se usa para combinar lo que fija la programacion con lo que trae el prompt
    /// planificado, que es mas especifico.
    /// </summary>
    public static Dictionary<string, string> Merge(params IReadOnlyDictionary<string, string>?[] layers)
    {
        var merged = NewMap();
        foreach (var layer in layers)
        {
            if (layer is null) continue;
            foreach (var (key, value) in layer)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                merged[key] = value;
            }
        }
        return merged;
    }

    /// <summary>Variables declaradas que se quedan sin valor en esta ejecucion.</summary>
    public static List<string> MissingKeys(
        IEnumerable<ProjectVariable>? definitions,
        IReadOnlyDictionary<string, string> resolved)
    {
        if (definitions is null) return [];
        return definitions
            .Where(d => IsValidKey(d.Key) && !resolved.ContainsKey(d.Key.Trim()))
            .Select(d => d.Key.Trim())
            .ToList();
    }

    // ── Sustitucion ──

    /// <summary>
    /// Reemplaza <c>{{clave}}</c> por su valor. Los marcadores que no correspondan
    /// a una variable con valor se dejan intactos: puede ser una plantilla ajena
    /// (Handlebars, Jinja, ejemplos de JSON) y romperla seria peor.
    /// </summary>
    public static string? Apply(string? text, IReadOnlyDictionary<string, string>? values)
    {
        if (string.IsNullOrEmpty(text) || values is null || values.Count == 0) return text;
        if (text.IndexOf("{{", StringComparison.Ordinal) < 0) return text;

        return PlaceholderPattern.Replace(text, m =>
            values.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);
    }

    /// <summary>
    /// Marcadores <c>{{clave}}</c> que siguen sin sustituir en un texto ya procesado,
    /// es decir, variables que esta ejecucion ha dejado sin valor. Sirve para que un
    /// modulo que depende del valor (y no solo lo cita en un prompt) pueda parar con
    /// un mensaje claro en vez de trabajar con el marcador como si fuese texto.
    /// </summary>
    public static List<string> UnresolvedKeys(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        return PlaceholderPattern.Matches(text)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Aplica la sustitucion a la configuracion combinada de un modulo (prompts,
    /// captions, condiciones...). Solo toca valores de texto de primer nivel; los
    /// numeros, booleanos y objetos anidados se copian sin cambios.
    /// </summary>
    public static Dictionary<string, object> ApplyToConfig(
        Dictionary<string, object> config,
        IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0) return config;

        var result = new Dictionary<string, object>(config, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, raw) in config)
        {
            switch (raw)
            {
                case string s:
                    result[key] = Apply(s, values) ?? s;
                    break;
                case JsonElement je when je.ValueKind == JsonValueKind.String:
                    var text = je.GetString();
                    if (text is not null) result[key] = Apply(text, values) ?? text;
                    break;
            }
        }

        return result;
    }

    // ── Inyeccion en el prompt ──

    /// <summary>
    /// Bloque etiquetado con las variables de la ejecucion, o null si no hay
    /// ninguna. <paramref name="definitions"/> es opcional y solo aporta la
    /// descripcion de cada variable.
    /// </summary>
    public static string? BuildBlock(
        IReadOnlyDictionary<string, string>? values,
        IEnumerable<ProjectVariable>? definitions = null)
    {
        if (values is null || values.Count == 0) return null;

        var descriptions = NewMap();
        if (definitions is not null)
        {
            foreach (var def in definitions)
            {
                if (!IsValidKey(def.Key) || string.IsNullOrWhiteSpace(def.Description)) continue;
                descriptions[def.Key.Trim()] = def.Description!.Trim();
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine(BlockHeader);
        sb.AppendLine(BlockSubtitle);
        foreach (var (key, value) in values.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var description = descriptions.TryGetValue(key, out var d) ? $" ({d})" : "";
            sb.AppendLine($"- {key}{description}: {value}");
        }

        return sb.ToString().TrimEnd();
    }
}
