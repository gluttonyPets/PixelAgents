namespace Server.Services.Ai;

/// <summary>
/// Modelo analista por defecto cuando un proyecto no tiene ninguno configurado.
/// El primario es gpt-4o-mini (OpenAI): barato y multimodal. Si el tenant no tiene
/// API Key de OpenAI, se cae a otro modelo multimodal disponible para que el
/// aprendizaje funcione sin necesidad de configurar nada.
/// </summary>
public static class AnalystDefaults
{
    /// <summary>Orden de preferencia (todos multimodales). El primero cuyo proveedor
    /// tenga API Key configurada se usa como modelo analista por defecto.</summary>
    private static readonly (string Provider, string Model)[] Preference =
    [
        ("OpenAI",    "gpt-4o-mini"),
        ("Anthropic", "claude-haiku-4-5-20251001"),
        ("Google",    "gemini-2.5-flash"),
    ];

    /// <summary>
    /// Devuelve el (proveedor, modelo) por defecto para un tenant, según qué proveedores
    /// tienen API Key. Null si ninguno de los preferidos está disponible.
    /// </summary>
    public static (string Provider, string Model)? Resolve(IEnumerable<string> providersWithKeys)
    {
        var set = new HashSet<string>(providersWithKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var pref in Preference)
            if (set.Contains(pref.Provider))
                return pref;
        return null;
    }
}
