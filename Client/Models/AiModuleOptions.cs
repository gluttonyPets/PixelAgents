namespace Client.Models;

/// <summary>
/// Opciones de configuracion de modulo que dependen del modelo elegido y que
/// tienen impacto directo en coste. Vive aqui y no dentro de cada .razor porque
/// la pagina de Modulos y el editor del pipeline tienen que ofrecer exactamente
/// lo mismo: cuando las dos listas se desincronizan es cuando se cuela un valor
/// que el proveedor no entiende y acaba facturandose el tramo mas caro.
/// </summary>
public static class AiModuleOptions
{
    /// <summary>
    /// gpt-image factura la imagen como tokens de salida. A 1024x1536 son 1.584
    /// tokens en "medium" contra 6.240 en "high", asi que el default importa.
    /// </summary>
    public const string DefaultImageQuality = "medium";

    public const string DefaultDallEQuality = "standard";

    public static bool IsGptImageModel(string? model) =>
        !string.IsNullOrWhiteSpace(model)
        && model!.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase);

    /// <summary>dall-e-2 no acepta el parametro quality; el resto si.</summary>
    public static bool SupportsQuality(string? model) =>
        !string.IsNullOrWhiteSpace(model)
        && !model!.Equals("dall-e-2", StringComparison.OrdinalIgnoreCase);

    public static string DefaultQualityForModel(string? model) =>
        IsGptImageModel(model) ? DefaultImageQuality : DefaultDallEQuality;

    /// <summary>
    /// Vocabularios distintos: gpt-image usa low/medium/high y DALL-E usa
    /// standard/hd. Mezclarlos es lo que hacia que "Standard" acabase generando
    /// en high, porque gpt-image no reconoce ese valor y cae en su default.
    /// </summary>
    private static readonly (string Value, string Label)[] GptImageQualities =
    [
        ("low",    "Baja (la mas barata)"),
        ("medium", "Media (recomendada)"),
        ("high",   "Alta (4x mas cara que media)")
    ];

    private static readonly (string Value, string Label)[] DallEQualities =
    [
        ("standard", "Standard"),
        ("hd",       "HD")
    ];

    public static (string Value, string Label)[] GetImageQualitiesForModel(string? model) =>
        IsGptImageModel(model) ? GptImageQualities : DallEQualities;

    /// <summary>
    /// Convierte un valor guardado al vocabulario del modelo actual, para que al
    /// abrir un modulo antiguo (o al cambiar de modelo) el selector no muestre un
    /// valor que ese modelo no acepta.
    /// </summary>
    public static string NormalizeQualityForModel(string? stored, string? model)
    {
        var q = stored?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(q)) return DefaultQualityForModel(model);

        if (IsGptImageModel(model))
        {
            return q switch
            {
                "low" or "medium" or "high" => q,
                "hd" => "high",
                _ => DefaultImageQuality
            };
        }

        return q switch
        {
            "standard" or "hd" => q,
            "high" => "hd",
            _ => DefaultDallEQuality
        };
    }

    /// <summary>
    /// gpt-5.x y la serie o aceptan reasoning_effort. gpt-5-chat es la variante
    /// sin razonamiento y lo rechaza, igual que gpt-4.x y anteriores.
    /// Debe coincidir con OpenAiProvider.SupportsReasoningEffort del servidor.
    /// </summary>
    public static bool SupportsReasoningEffort(string? providerType, string? model)
    {
        if (!string.Equals(providerType, "OpenAI", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(model)) return false;
        if (model!.Contains("chat", StringComparison.OrdinalIgnoreCase)) return false;

        return model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Vacio = no se manda el parametro y decide el proveedor (medium en OpenAI).
    /// Los tokens de razonamiento se facturan como salida, asi que bajar el
    /// esfuerzo es la palanca mas directa sobre el coste de estos modelos.
    /// </summary>
    public static readonly (string Value, string Label)[] ReasoningEffortOptions =
    [
        ("",        "Por defecto del proveedor"),
        ("none",    "Ninguno (sin razonamiento)"),
        ("minimal", "Minimo"),
        ("low",     "Bajo (recomendado para generar contenido)"),
        ("medium",  "Medio"),
        ("high",    "Alto (el mas caro)")
    ];
}
