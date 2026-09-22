namespace Server.Services.Ai;

/// <summary>
/// Opcion "Busqueda en internet" de los modulos de texto. Se guarda en la config
/// del modulo como <c>"webSearch": true|false</c>. El cliente la escribe siempre
/// al crear un modulo (marcada por defecto); una config sin la clave, como la de
/// los modulos creados antes de existir la opcion, cuenta como desactivada para
/// no cambiar el coste ni el comportamiento de lo que ya estaba funcionando.
/// </summary>
public static class WebSearchOption
{
    public const string ConfigKey = "webSearch";

    /// <summary>Precio orientativo por busqueda (Anthropic y OpenAI: 10 USD / 1000).</summary>
    public const decimal CostPerSearch = 0.01m;

    /// <summary>Proveedores cuyo modulo de texto sabe activar la busqueda.</summary>
    public static readonly IReadOnlySet<string> SupportedProviders =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Anthropic", "OpenAI", "Google" };

    public static bool IsEnabled(IReadOnlyDictionary<string, object>? config) =>
        config is not null
        && config.TryGetValue(ConfigKey, out var value)
        && value switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var parsed) && parsed,
            _ => false,
        };

    /// <summary>
    /// Herramientas de servidor de Anthropic para el modelo. Los modelos 4.6 en
    /// adelante usan las variantes con filtrado dinamico e incluyen web_fetch
    /// (leer una URL concreta); los anteriores solo la busqueda basica.
    /// </summary>
    public static List<Dictionary<string, object>> AnthropicTools(string modelName)
    {
        if (SupportsDynamicWebTools(modelName))
        {
            return
            [
                new() { ["type"] = "web_search_20260209", ["name"] = "web_search", ["max_uses"] = 5 },
                new() { ["type"] = "web_fetch_20260209", ["name"] = "web_fetch", ["max_uses"] = 10 },
            ];
        }

        return
        [
            new() { ["type"] = "web_search_20250305", ["name"] = "web_search", ["max_uses"] = 5 },
        ];
    }

    private static readonly string[] DynamicWebToolPrefixes =
    [
        "claude-opus-4-6", "claude-opus-4-7", "claude-opus-4-8", "claude-opus-5",
        "claude-sonnet-4-6", "claude-sonnet-5",
        "claude-fable-", "claude-mythos-",
    ];

    public static bool SupportsDynamicWebTools(string? modelName) =>
        !string.IsNullOrWhiteSpace(modelName)
        && DynamicWebToolPrefixes.Any(p => modelName.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}
