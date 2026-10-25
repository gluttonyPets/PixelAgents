using System.Globalization;
using System.Text.Json;

namespace Server.Services.Ai;

/// <summary>
/// Peticion de texto a voz de ElevenLabs (<c>POST /v1/text-to-speech/{voice_id}</c>).
///
/// Va aparte del proveedor, igual que <see cref="OpenAiSpeech"/>, para probar sin
/// red que se manda: la voz va en la ruta (es un id, no un nombre), y el resto
/// del control (velocidad, estabilidad, parecido, estilo) en <c>voice_settings</c>.
/// La configuracion del modulo usa las mismas claves que el de OpenAI donde
/// significan lo mismo (<c>voice</c>, <c>speed</c>) para que el handler y la
/// demostracion de voz no distingan proveedor.
/// </summary>
public static class ElevenLabsSpeech
{
    public const string BaseUrl = "https://api.elevenlabs.io";

    /// <summary>
    /// "George", una de las voces predeterminadas que tiene toda cuenta. Solo se
    /// usa si el modulo no tiene voz elegida: la UI siempre guarda una.
    /// </summary>
    public const string DefaultVoice = "JBFqnCBsd6RMkjVDRZzb";

    public const string StabilityKey = "stability";
    public const string SimilarityKey = "similarityBoost";
    public const string StyleKey = "style";

    /// <summary>Rango de <c>speed</c> que acepta la API; fuera de el responde 400.</summary>
    public const double MinSpeed = 0.7, MaxSpeed = 1.2;

    /// <summary>
    /// Caracteres por peticion de cada modelo (elevenlabs.io/docs/models,
    /// revisado el 2026-10-25). Un modelo que no este aqui usa el limite mas bajo
    /// de la familia para no fallar a mitad de locucion.
    /// </summary>
    private static readonly Dictionary<string, int> MaxCharsByModel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eleven_v4"] = 10_000,
        ["eleven_v3"] = 5_000,
        ["eleven_multilingual_v2"] = 10_000,
        ["eleven_flash_v2_5"] = 40_000,
    };

    private const int FallbackMaxChars = 5_000;

    public static int MaxInputChars(string model) =>
        MaxCharsByModel.TryGetValue(model, out var max) ? max : FallbackMaxChars;

    /// <summary>eleven_v3 no admite velocidad: la documentacion la excluye.</summary>
    public static bool AcceptsSpeed(string model) =>
        !model.Equals("eleven_v3", StringComparison.OrdinalIgnoreCase);

    public static string VoiceId(IReadOnlyDictionary<string, object> config) =>
        ReadString(config, "voice") ?? DefaultVoice;

    /// <summary>URL de la peticion para la voz configurada, con salida mp3.</summary>
    public static string BuildRequestUrl(IReadOnlyDictionary<string, object> config) =>
        $"{BaseUrl}/v1/text-to-speech/{Uri.EscapeDataString(VoiceId(config))}?output_format=mp3_44100_128";

    /// <summary>Cuerpo JSON de la peticion para <paramref name="text"/>.</summary>
    public static string BuildRequestJson(string model, string text, IReadOnlyDictionary<string, object> config)
    {
        // Solo se mandan los ajustes configurados: lo que falta lo pone la voz con
        // los valores que tenga guardados en ElevenLabs.
        var settings = new Dictionary<string, object>();
        if (ReadDouble(config, StabilityKey) is double stability) settings["stability"] = Math.Clamp(stability, 0, 1);
        if (ReadDouble(config, SimilarityKey) is double similarity) settings["similarity_boost"] = Math.Clamp(similarity, 0, 1);
        if (ReadDouble(config, StyleKey) is double style) settings["style"] = Math.Clamp(style, 0, 1);
        if (AcceptsSpeed(model) && ReadDouble(config, "speed") is double speed && Math.Abs(speed - 1d) >= 0.001)
            settings["speed"] = Math.Clamp(speed, MinSpeed, MaxSpeed);

        var body = new Dictionary<string, object>
        {
            ["text"] = text,
            ["model_id"] = model,
        };
        if (settings.Count > 0) body["voice_settings"] = settings;

        return JsonSerializer.Serialize(body);
    }

    /// <summary>Coste de una locucion: ElevenLabs factura por caracter.</summary>
    public static decimal EstimateCost(string model, int characters)
    {
        if (characters <= 0) return 0m;
        var rate = PricingCatalog.GetAuxiliaryRate(model);
        if (rate is null || !rate.Unit.Equals("1M caracteres", StringComparison.OrdinalIgnoreCase)) return 0m;
        return rate.Amount * characters / 1_000_000m;
    }

    /// <summary>
    /// Voces de la cuenta (respuesta de <c>GET /v2/voices</c>), con una etiqueta
    /// legible: "Nombre · genero, acento, idioma".
    /// </summary>
    public static List<(string Id, string Label)> ParseVoices(string json, out string? nextPageToken)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        nextPageToken = root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True
            && root.TryGetProperty("next_page_token", out var token) && token.ValueKind == JsonValueKind.String
                ? token.GetString()
                : null;

        var voices = new List<(string, string)>();
        if (!root.TryGetProperty("voices", out var list) || list.ValueKind != JsonValueKind.Array) return voices;

        foreach (var v in list.EnumerateArray())
        {
            var id = v.TryGetProperty("voice_id", out var vid) ? vid.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var name = v.TryGetProperty("name", out var n) ? n.GetString() : null;

            var details = new List<string>();
            if (v.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "gender", "accent", "language" })
                    if (labels.TryGetProperty(key, out var l) && l.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(l.GetString()))
                        details.Add(l.GetString()!);
            }
            if (v.TryGetProperty("category", out var cat) && cat.GetString() is "cloned" or "professional")
                details.Add("clonada");

            var label = string.IsNullOrWhiteSpace(name) ? id : name!;
            if (details.Count > 0) label += " · " + string.Join(", ", details);
            voices.Add((id!, label));
        }

        return voices;
    }

    private static double? ReadDouble(IReadOnlyDictionary<string, object> config, string key)
    {
        if (!config.TryGetValue(key, out var raw)) return null;
        return raw switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            JsonElement { ValueKind: JsonValueKind.Number } je => je.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } je
                when double.TryParse(je.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
            string str when double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
            _ => null,
        };
    }

    private static string? ReadString(IReadOnlyDictionary<string, object> config, string key)
    {
        if (!config.TryGetValue(key, out var raw)) return null;
        var value = raw is JsonElement { ValueKind: JsonValueKind.String } je ? je.GetString() : raw as string;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
