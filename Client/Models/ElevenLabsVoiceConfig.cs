using System.Globalization;
using System.Text.Json;

namespace Client.Models;

/// <summary>
/// Configuracion de un modulo de voz de ElevenLabs. Se guarda como JSON en
/// <c>AiModule.Configuration</c>; las claves coinciden con las que lee
/// <c>ElevenLabsSpeech</c> en el servidor. <c>voice</c> y <c>speed</c> son las
/// mismas que en el modulo de voz de OpenAI.
/// </summary>
public class ElevenLabsVoiceConfig
{
    public const string Provider = "ElevenLabs";

    /// <summary>Rango de velocidad que acepta la API (fuera de el responde 400).</summary>
    public const double MinSpeed = 0.7, MaxSpeed = 1.2;

    /// <summary>Id de la voz en ElevenLabs. Vacio = la predeterminada del servidor.</summary>
    public string Voice { get; set; } = "";
    /// <summary>Nombre legible de la voz, para mostrarlo sin llamar a ElevenLabs.</summary>
    public string VoiceName { get; set; } = "";
    public double Speed { get; set; } = 1.0;
    // Valores por defecto de la API de ElevenLabs.
    public double Stability { get; set; } = 0.5;
    public double SimilarityBoost { get; set; } = 0.75;
    public double Style { get; set; }

    public static bool IsElevenLabsAudio(string? providerType, string? moduleType) =>
        providerType == Provider && moduleType == "Audio";

    /// <summary>eleven_v3 no admite velocidad. Misma regla que el servidor.</summary>
    public static bool SupportsSpeed(string? model) => model != "eleven_v3";

    public string ToJson()
    {
        var map = new Dictionary<string, object>
        {
            ["stability"] = Round(Stability),
            ["similarityBoost"] = Round(SimilarityBoost),
            ["style"] = Round(Style),
        };
        if (!string.IsNullOrWhiteSpace(Voice)) map["voice"] = Voice.Trim();
        if (!string.IsNullOrWhiteSpace(VoiceName)) map["voiceName"] = VoiceName.Trim();
        if (Math.Abs(Speed - 1.0) >= 0.001) map["speed"] = Round(Math.Clamp(Speed, MinSpeed, MaxSpeed));
        return JsonSerializer.Serialize(map);
    }

    public static ElevenLabsVoiceConfig FromJson(string? json)
    {
        var cfg = new ElevenLabsVoiceConfig();
        if (string.IsNullOrWhiteSpace(json)) return cfg;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            cfg.Voice = ReadString(root, "voice") ?? "";
            cfg.VoiceName = ReadString(root, "voiceName") ?? "";
            cfg.Speed = ReadDouble(root, "speed") ?? cfg.Speed;
            cfg.Stability = ReadDouble(root, "stability") ?? cfg.Stability;
            cfg.SimilarityBoost = ReadDouble(root, "similarityBoost") ?? cfg.SimilarityBoost;
            cfg.Style = ReadDouble(root, "style") ?? cfg.Style;
        }
        catch (JsonException) { }

        return cfg;
    }

    private static double Round(double value) => Math.Round(value, 2);

    private static string? ReadString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? ReadDouble(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }
}
