using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Client.Models;

/// <summary>
/// Configuracion de un modulo mientras se crea o se edita en la pagina Modulos.
/// Crear y editar usan esta misma clase y el mismo formulario
/// (<c>ModuleConfigForm</c>): eran dos copias que se habian ido separando, y cada
/// diferencia era un campo que se podia elegir al crear pero no al editar, o que
/// se perdia al guardar.
///
/// Guarda los campos de todos los tipos a la vez. Asi, al cambiar de proveedor o de
/// modelo en el editor se conserva lo que siga teniendo sentido (el system prompt
/// de un texto que pasa de OpenAI a Anthropic) y <see cref="ToJson"/> escribe solo
/// las claves del tipo y proveedor elegidos.
/// </summary>
public class ModuleConfigDraft
{
    // ── Texto, orquestador y coordinador ──
    public string SystemPrompt { get; set; } = "";
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 4096;
    public bool WebSearch { get; set; }
    public string ReasoningEffort { get; set; } = "";

    // ── Imagen ──
    public string ImageSize { get; set; } = "1024x1024";
    public string ImageQuality { get; set; } = AiModuleOptions.DefaultImageQuality;

    /// <summary>Imagenes por llamada en gpt-image y Grok (clave "n").</summary>
    public int ImageCount { get; set; } = 1;

    public string GeminiAspectRatio { get; set; } = "1:1";
    public int GeminiImageCount { get; set; } = 1;
    public int LeoWidth { get; set; } = 1024;
    public int LeoHeight { get; set; } = 1024;
    public string LeoPresetStyle { get; set; } = "DYNAMIC";

    // ── Video ──
    public VideoModuleConfig Video { get; set; } = new();

    // ── Audio ──
    public string TtsVoice { get; set; } = "alloy";
    public double TtsSpeed { get; set; } = 1.0;
    public string TtsInstructions { get; set; } = "";
    public ElevenLabsVoiceConfig ElevenLabs { get; set; } = new();

    // ── Transcripcion ──
    public string TranscriptionLang { get; set; } = "";
    public string TranscriptionFormat { get; set; } = "json";

    // ── Embeddings ──
    public int? EmbeddingDimensions { get; set; }

    // ── Canva ──
    public string CanvaAccessToken { get; set; } = "";
    public string CanvaExportFormat { get; set; } = "png";
    public string CanvaBrandTemplate { get; set; } = "";
    public string CanvaDesignType { get; set; } = "doc";

    // ── Search Console ──
    public SearchConsoleConfig SearchConsole { get; set; } = new();

    // ── Texto plano ──
    public string StaticTextContent { get; set; } = "";

    /// <summary>
    /// Configuracion de un modulo nuevo. La busqueda en internet arranca activada:
    /// es lo que se espera de un modulo de texto recien creado.
    /// </summary>
    public static ModuleConfigDraft ForNew(string? model) => new()
    {
        WebSearch = true,
        ImageQuality = AiModuleOptions.DefaultQualityForModel(model),
    };

    /// <summary>
    /// Configuracion guardada de un modulo. Un modulo sin la clave "webSearch" es
    /// anterior a la opcion y el servidor lo trata como desactivado, asi que aqui
    /// tambien.
    /// </summary>
    public static ModuleConfigDraft FromJson(string? json, string? model)
    {
        var d = new ModuleConfigDraft
        {
            ImageQuality = AiModuleOptions.DefaultQualityForModel(model),
            Video = VideoModuleConfig.FromJson(json),
            ElevenLabs = ElevenLabsVoiceConfig.FromJson(json),
            SearchConsole = SearchConsoleConfig.FromJson(json),
        };
        if (string.IsNullOrWhiteSpace(json)) return d;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return d;

            string? Str(string key) =>
                root.TryGetProperty(key, out var v)
                    ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
                    : null;
            double? Num(string key) =>
                double.TryParse(Str(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
            int? Int(string key) => Num(key) is { } n ? (int)Math.Round(n) : null;

            d.SystemPrompt = Str("systemPrompt") ?? "";
            d.Temperature = Num("temperature") ?? d.Temperature;
            d.MaxTokens = Int("maxTokens") ?? d.MaxTokens;
            d.WebSearch = Str("webSearch") == "true";
            d.ReasoningEffort = Str("reasoningEffort") ?? "";

            d.ImageSize = Str("size") ?? d.ImageSize;
            if (Str("quality") is { } quality)
                d.ImageQuality = AiModuleOptions.NormalizeQualityForModel(quality, model);
            d.ImageCount = Int("n") ?? d.ImageCount;
            d.GeminiAspectRatio = Str("aspectRatio") ?? d.GeminiAspectRatio;
            d.GeminiImageCount = Int("numberOfImages") ?? d.GeminiImageCount;
            d.LeoWidth = Int("width") ?? d.LeoWidth;
            d.LeoHeight = Int("height") ?? d.LeoHeight;
            d.LeoPresetStyle = Str("presetStyle") ?? d.LeoPresetStyle;

            d.TtsVoice = Str("voice") ?? d.TtsVoice;
            d.TtsSpeed = Num("speed") ?? d.TtsSpeed;
            d.TtsInstructions = Str("voiceInstructions") ?? "";

            d.TranscriptionLang = Str("language") ?? "";
            d.TranscriptionFormat = Str("response_format") ?? d.TranscriptionFormat;
            d.EmbeddingDimensions = Int("dimensions");

            d.CanvaAccessToken = Str("accessToken") ?? "";
            d.CanvaExportFormat = Str("exportFormat") ?? d.CanvaExportFormat;
            d.CanvaBrandTemplate = Str("brandTemplateId") ?? "";
            d.CanvaDesignType = Str("designType") ?? d.CanvaDesignType;

            d.StaticTextContent = Str("content") ?? "";
        }
        catch (JsonException) { }

        return d;
    }

    /// <summary>
    /// Reencuadra lo que depende del modelo al elegir otro: tamano y calidad de
    /// imagen validos para el nuevo, y duracion, resolucion y formato de video.
    /// </summary>
    public void AdaptToModel(string? model, VideoModelOptionsResponse? videoOptions)
    {
        var sizes = AiModuleOptions.ImageSizesForModel(model);
        if (!sizes.Contains(ImageSize)) ImageSize = sizes[0];
        // Cada familia usa un vocabulario de calidad distinto: reencuadrar el valor
        // evita guardar uno que el modelo nuevo no entiende.
        ImageQuality = AiModuleOptions.NormalizeQualityForModel(ImageQuality, model);
        Video.Normalize(videoOptions);
    }

    /// <summary>
    /// Si el formulario edita la configuracion de este tipo y proveedor. En el resto
    /// (archivos, directorio, escena...) la configuracion guardada no se toca.
    /// </summary>
    public static bool Handles(string? provider, string? moduleType) => ManagedKeys(provider, moduleType) is not null;

    /// <summary>
    /// Claves que escribe el formulario para ese tipo y proveedor. Al guardar se
    /// sustituyen y el resto de las que hubiera en el modulo se conservan: el
    /// formulario no puede borrar lo que no sabe mostrar. Vacio = reemplaza todo.
    /// </summary>
    public static IReadOnlyCollection<string>? ManagedKeys(string? provider, string? moduleType)
    {
        if (moduleType == SearchConsoleConfig.ModuleType || ElevenLabsVoiceConfig.IsElevenLabsAudio(provider, moduleType))
            return [];

        return moduleType switch
        {
            "Text" or "Orchestrator" or "Coordinator" => ["systemPrompt", "temperature", "maxTokens", "reasoningEffort", "webSearch"],
            "Image" when provider == "Google" => ["systemPrompt", "aspectRatio", "numberOfImages"],
            "Image" when provider == "xAI" => ["systemPrompt", "n"],
            "Image" when provider == "LeonardoAI" => ["systemPrompt", "width", "height", "presetStyle"],
            "Image" => ["systemPrompt", "size", "quality", "n"],
            VideoModuleConfig.ModuleType => VideoModuleConfig.Keys,
            "Audio" => ["voice", "speed", "voiceInstructions"],
            "Transcription" => ["language", "response_format"],
            "Embeddings" => ["dimensions"],
            "Design" when provider == "Canva" => ["accessToken", "exportFormat", "brandTemplateId", "designType"],
            "StaticText" => ["content"],
            _ => null,
        };
    }

    /// <summary>
    /// JSON a guardar para ese tipo, proveedor y modelo. Con <paramref name="original"/>
    /// (la configuracion guardada del modulo, si sigue siendo del mismo tipo y
    /// proveedor) se conservan las claves que el formulario no gestiona.
    /// </summary>
    public string? ToJson(string? provider, string? moduleType, string? model,
        VideoModelOptionsResponse? videoOptions, string? original = null)
    {
        var managed = ManagedKeys(provider, moduleType);
        if (managed is null) return original;

        var built = Build(provider, moduleType, model, videoOptions);
        return managed.Count == 0 ? built : MergeInto(original, built, managed);
    }

    private string? Build(string? provider, string? moduleType, string? model, VideoModelOptionsResponse? videoOptions)
    {
        if (moduleType == SearchConsoleConfig.ModuleType) return SearchConsole.ToJson();
        if (ElevenLabsVoiceConfig.IsElevenLabsAudio(provider, moduleType)) return ElevenLabs.ToJson();
        if (moduleType == VideoModuleConfig.ModuleType) return Video.ToJson(videoOptions);

        var map = new JsonObject();
        void Text(string key, string value) { if (!string.IsNullOrWhiteSpace(value)) map[key] = value; }

        switch (moduleType)
        {
            case "Text" or "Orchestrator" or "Coordinator":
                Text("systemPrompt", SystemPrompt);
                if (Temperature != 0.7) map["temperature"] = Temperature;
                if (MaxTokens != 4096) map["maxTokens"] = MaxTokens;
                if (AiModuleOptions.SupportsReasoningEffort(provider, model)) Text("reasoningEffort", ReasoningEffort);
                // Se escribe siempre, tambien en false: un modulo sin la clave es uno
                // anterior a la opcion y el servidor lo trata como desactivado.
                if (AiModuleOptions.SupportsWebSearch(provider, moduleType)) map["webSearch"] = WebSearch;
                break;

            case "Image" when provider == "Google":
                Text("systemPrompt", SystemPrompt);
                if (GeminiAspectRatio != "1:1") map["aspectRatio"] = GeminiAspectRatio;
                if (GeminiImageCount != 1) map["numberOfImages"] = GeminiImageCount;
                break;

            case "Image" when provider == "xAI":
                Text("systemPrompt", SystemPrompt);
                if (ImageCount != 1) map["n"] = ImageCount;
                break;

            case "Image" when provider == "LeonardoAI":
                Text("systemPrompt", SystemPrompt);
                if (LeoWidth != 1024) map["width"] = LeoWidth;
                if (LeoHeight != 1024) map["height"] = LeoHeight;
                if (LeoPresetStyle != "DYNAMIC") map["presetStyle"] = LeoPresetStyle;
                break;

            case "Image":
                Text("systemPrompt", SystemPrompt);
                if (ImageSize != "1024x1024") map["size"] = ImageSize;
                // La calidad se escribe siempre: si se omite, la API aplica "auto",
                // que en gpt-image resuelve al tramo mas caro (high).
                if (provider == "OpenAI" && AiModuleOptions.SupportsQuality(model)) map["quality"] = ImageQuality;
                if (AiModuleOptions.IsGptImageModel(model) && ImageCount != 1) map["n"] = ImageCount;
                break;

            case "Audio":
                map["voice"] = TtsVoice;
                if (TtsSpeed != 1.0) map["speed"] = TtsSpeed;
                // Se conservan aunque el modelo no las use (tts-1): si se vuelve a
                // gpt-4o-mini-tts siguen ahi, y el servidor no las manda a tts-1.
                Text("voiceInstructions", TtsInstructions);
                break;

            case "Transcription":
                Text("language", TranscriptionLang);
                if (TranscriptionFormat != "json") map["response_format"] = TranscriptionFormat;
                break;

            case "Embeddings":
                if (EmbeddingDimensions is { } dims) map["dimensions"] = dims;
                break;

            case "Design" when provider == "Canva":
                Text("accessToken", CanvaAccessToken);
                if (CanvaExportFormat != "png") map["exportFormat"] = CanvaExportFormat;
                Text("brandTemplateId", CanvaBrandTemplate);
                if (CanvaDesignType != "doc") map["designType"] = CanvaDesignType;
                break;

            case "StaticText":
                Text("content", StaticTextContent);
                break;
        }

        return map.Count > 0 ? map.ToJsonString() : null;
    }

    /// <summary>
    /// <paramref name="built"/> encima de <paramref name="original"/>: las claves de
    /// <paramref name="managedKeys"/> salen de lo construido (si no estan, es que
    /// valen lo de por defecto y se quitan) y el resto se conserva tal cual.
    /// </summary>
    public static string? MergeInto(string? original, string? built, IReadOnlyCollection<string> managedKeys)
    {
        if (string.IsNullOrWhiteSpace(original)) return built;

        JsonObject result;
        try
        {
            if (JsonNode.Parse(original) is not JsonObject parsed) return built;
            result = parsed;
        }
        catch (JsonException)
        {
            return built;
        }

        foreach (var key in managedKeys) result.Remove(key);

        if (!string.IsNullOrWhiteSpace(built) && JsonNode.Parse(built) is JsonObject fresh)
        {
            foreach (var (key, value) in fresh.ToList())
            {
                fresh.Remove(key);
                result[key] = value;
            }
        }

        return result.Count > 0 ? result.ToJsonString() : null;
    }
}
