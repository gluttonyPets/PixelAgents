using System.Globalization;
using System.Text.Json;

namespace Client.Models;

/// <summary>
/// Configuracion del modulo de subtitulos (palabra a palabra, estilo TikTok). Las
/// claves son las que lee <c>SubtitleBuilder.Style.FromConfig</c> en el servidor.
/// Los porcentajes son de la altura del video, asi se ven igual a cualquier
/// resolucion.
/// </summary>
public class SubtitlesConfig
{
    public const string ModuleType = "Subtitles";

    public static readonly string[] Keys =
        ["subtitleWords", "subtitleColor", "subtitleSize", "subtitlePosition", "subtitleUppercase", "language"];

    public int WordsPerGroup { get; set; } = 3;
    public string HighlightColor { get; set; } = "#FFE600";
    public double SizePercent { get; set; } = 6.5;
    public double PositionPercent { get; set; } = 65;
    public bool Uppercase { get; set; } = true;

    /// <summary>Idioma del audio. Solo se usa si no llega el texto de la locucion y hay que transcribir.</summary>
    public string Language { get; set; } = "es";

    public static readonly (string Value, string Label)[] HighlightOptions =
    [
        ("#FFE600", "Amarillo"), ("#39FF14", "Verde"), ("#00E5FF", "Cian"),
        ("#FF3EA5", "Rosa"), ("#FF8A00", "Naranja"), ("#FFFFFF", "Blanco"),
    ];

    public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["subtitleWords"] = Math.Clamp(WordsPerGroup, 1, 5),
        ["subtitleColor"] = HighlightColor,
        ["subtitleSize"] = Math.Round(Math.Clamp(SizePercent, 3, 10), 1),
        ["subtitlePosition"] = Math.Round(Math.Clamp(PositionPercent, 15, 90)),
        ["subtitleUppercase"] = Uppercase,
        ["language"] = string.IsNullOrWhiteSpace(Language) ? "es" : Language.Trim(),
    });

    public static SubtitlesConfig FromJson(string? json)
    {
        var cfg = new SubtitlesConfig();
        if (string.IsNullOrWhiteSpace(json)) return cfg;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return cfg;

            if (Number(root, "subtitleWords") is { } w) cfg.WordsPerGroup = (int)Math.Clamp(w, 1, 5);
            if (root.TryGetProperty("subtitleColor", out var c) && c.ValueKind == JsonValueKind.String
                && c.GetString() is { Length: 7 } color && color[0] == '#')
                cfg.HighlightColor = color.ToUpperInvariant();
            if (Number(root, "subtitleSize") is { } s) cfg.SizePercent = Math.Clamp(s, 3, 10);
            if (Number(root, "subtitlePosition") is { } p) cfg.PositionPercent = Math.Clamp(p, 15, 90);
            if (root.TryGetProperty("subtitleUppercase", out var u) && u.ValueKind is JsonValueKind.True or JsonValueKind.False)
                cfg.Uppercase = u.GetBoolean();
            if (root.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(l.GetString()))
                cfg.Language = l.GetString()!;
        }
        catch (JsonException) { /* configuracion rota: valores por defecto */ }
        return cfg;
    }

    private static double? Number(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        } : null;
}
