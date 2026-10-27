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
    /// <summary>
    /// Dueno publico si la voz es de la biblioteca de ElevenLabs: el servidor la
    /// anade a la cuenta la primera vez que locuta con ella. Vacio = voz de la cuenta.
    /// </summary>
    public string VoiceOwner { get; set; } = "";
    /// <summary>Idioma en el que se buscan voces de la biblioteca (ISO 639-1). Vacio = solo las de la cuenta.</summary>
    public string Language { get; set; } = "es";
    /// <summary>Acento por el que se filtra la biblioteca, tal cual lo da ElevenLabs. Vacio = todos.</summary>
    public string Accent { get; set; } = "";
    /// <summary>Uso (estilo) por el que se filtra la biblioteca, tal cual lo da ElevenLabs. Vacio = todos.</summary>
    public string UseCase { get; set; } = "";

    /// <summary>
    /// Nombre en castellano de los acentos de la biblioteca. ElevenLabs no publica
    /// la lista: los valores salen de sus voces y los que no esten aqui se muestran
    /// tal cual.
    /// </summary>
    private static readonly Dictionary<string, string> AccentLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["peninsular"] = "Castellano (Espana)",
        ["castilian"] = "Castellano (Espana)",
        ["spanish castilian"] = "Castellano (Espana)",
        ["spain"] = "Castellano (Espana)",
        ["andalusian"] = "Andaluz",
        ["canarian"] = "Canario",
        ["latin american"] = "Latino neutro",
        ["latin"] = "Latino neutro",
        ["neutral"] = "Neutro",
        ["standard"] = "Estandar",
        ["argentine"] = "Argentino",
        ["argentinian"] = "Argentino",
        ["rioplatense"] = "Rioplatense",
        ["mexican"] = "Mexicano",
        ["colombian"] = "Colombiano",
        ["chilean"] = "Chileno",
        ["venezuelan"] = "Venezolano",
        ["peruvian"] = "Peruano",
        ["cuban"] = "Cubano",
        ["puerto rican"] = "Puertorriqueno",
        ["dominican"] = "Dominicano",
        ["caribbean"] = "Caribeno",
        ["uruguayan"] = "Uruguayo",
        ["ecuadorian"] = "Ecuatoriano",
        ["american"] = "Estadounidense",
        ["british"] = "Britanico",
        ["brazilian"] = "Brasileno",
        ["portuguese"] = "Portugues (Portugal)",
    };

    /// <summary>Nombre en castellano de los usos (estilos) de la biblioteca.</summary>
    private static readonly Dictionary<string, string> UseCaseLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["narrative_story"] = "Narracion e historias",
        ["conversational"] = "Conversacional",
        ["characters_animation"] = "Personajes y animacion",
        ["social_media"] = "Redes sociales",
        ["entertainment_tv"] = "Entretenimiento y TV",
        ["advertisement"] = "Anuncios",
        ["informative_educational"] = "Informativo y educativo",
        ["news"] = "Noticias",
        ["meditation"] = "Meditacion",
        ["audiobook"] = "Audiolibros",
    };

    public static string AccentLabel(string value) => Humanize(value, AccentLabels);
    public static string UseCaseLabel(string value) => Humanize(value, UseCaseLabels);

    private static string Humanize(string value, Dictionary<string, string> labels)
    {
        if (labels.TryGetValue(value, out var label)) return label;
        var text = value.Replace('_', ' ').Trim();
        return text.Length == 0 ? value : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>
    /// Idiomas para buscar voces nativas en la biblioteca. Las voces predeterminadas
    /// de ElevenLabs son casi todas en ingles.
    /// </summary>
    public static readonly (string Value, string Label)[] LanguageOptions =
    [
        ("es", "Espanol"), ("en", "Ingles"), ("ca", "Catalan"), ("pt", "Portugues"),
        ("fr", "Frances"), ("it", "Italiano"), ("de", "Aleman"), ("nl", "Neerlandes"),
        ("pl", "Polaco"), ("ar", "Arabe"), ("zh", "Chino"), ("ja", "Japones"),
        ("", "Solo mis voces"),
    ];
    public double Speed { get; set; } = 1.0;
    // Valores por defecto de la API de ElevenLabs.
    public double Stability { get; set; } = 0.5;
    public double SimilarityBoost { get; set; } = 0.75;
    public double Style { get; set; }

    public static bool IsElevenLabsAudio(string? providerType, string? moduleType) =>
        providerType == Provider && moduleType == "Audio";

    /// <summary>eleven_v3 no admite velocidad. Misma regla que el servidor.</summary>
    public static bool SupportsSpeed(string? model) => model != "eleven_v3";

    /// <summary>
    /// Si el modelo se puede elegir en PixelAgents. Las muestras de la biblioteca
    /// pueden estar grabadas con modelos de ElevenLabs que aqui no se ofrecen
    /// (Turbo v2.5, Flash v2...), y esos no se pueden recomendar.
    /// </summary>
    public static bool IsOfferedModel(string? model) =>
        Client.Pages.Modules.AllModels.Any(m => m.Provider == Provider && m.Id == model);

    /// <summary>Nombre legible de un modelo de ElevenLabs, ofrezca PixelAgents o no.</summary>
    public static string ModelLabel(string? model) =>
        Client.Pages.Modules.AllModels.FirstOrDefault(m => m.Provider == Provider && m.Id == model)?.DisplayName
        ?? model switch
        {
            "eleven_turbo_v2_5" => "Eleven Turbo v2.5",
            "eleven_turbo_v2" => "Eleven Turbo v2",
            "eleven_flash_v2" => "Eleven Flash v2",
            "eleven_multilingual_v1" => "Eleven Multilingual v1",
            "eleven_monolingual_v1" => "Eleven English v1",
            _ => model ?? "",
        };

    public string ToJson()
    {
        var map = new Dictionary<string, object>
        {
            ["stability"] = Unit(Stability),
            ["similarityBoost"] = Unit(SimilarityBoost),
            ["style"] = Unit(Style),
        };
        if (!string.IsNullOrWhiteSpace(Voice)) map["voice"] = Voice.Trim();
        if (!string.IsNullOrWhiteSpace(VoiceName)) map["voiceName"] = VoiceName.Trim();
        if (!string.IsNullOrWhiteSpace(VoiceOwner)) map["voiceOwner"] = VoiceOwner.Trim();
        map["voiceLanguage"] = Language;
        if (!string.IsNullOrWhiteSpace(Accent)) map["voiceAccent"] = Accent;
        if (!string.IsNullOrWhiteSpace(UseCase)) map["voiceUseCase"] = UseCase;
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
            cfg.VoiceOwner = ReadString(root, "voiceOwner") ?? "";
            cfg.Language = ReadString(root, "voiceLanguage") ?? cfg.Language;
            cfg.Accent = ReadString(root, "voiceAccent") ?? "";
            cfg.UseCase = ReadString(root, "voiceUseCase") ?? "";
            cfg.Speed = ReadDouble(root, "speed") ?? cfg.Speed;
            // Acotados al leer: una version anterior del formulario podia guardar
            // 45 en vez de 0.45 (ver los sliders de ElevenLabsVoiceEditor).
            cfg.Stability = Unit(ReadDouble(root, "stability") ?? cfg.Stability);
            cfg.SimilarityBoost = Unit(ReadDouble(root, "similarityBoost") ?? cfg.SimilarityBoost);
            cfg.Style = Unit(ReadDouble(root, "style") ?? cfg.Style);
        }
        catch (JsonException) { }

        return cfg;
    }

    private static double Round(double value) => Math.Round(value, 2);

    /// <summary>Ajustes de voz: entre 0 y 1, como los acepta la API.</summary>
    private static double Unit(double value) => Round(Math.Clamp(value, 0, 1));

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
