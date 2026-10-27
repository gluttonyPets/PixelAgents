using System.Text.Json;

namespace Server.Services.Ai;

/// <summary>
/// Que parte de la configuracion es del modulo del catalogo y cual del nodo.
///
/// Un nodo del pipeline (ProjectModule) tiene su propia configuracion, que al
/// ejecutar va por encima de la del modulo. Ahi van cosas del nodo: saltarlo, su
/// numero de salidas, el loop, la publicacion, las condiciones, la carpeta... Pero
/// al anadir un modulo a un pipeline se copiaba en el nodo TODA la configuracion
/// del modulo, y esa copia ganaba despues a cualquier cambio del modulo: se elegia
/// otra voz, otra estabilidad u otro prompt de imagen y el nodo seguia con lo de
/// cuando se anadio.
///
/// Las claves de aqui solo se editan en el modulo (pagina Modulos y popup "Editar
/// nodo"), nunca por nodo, asi que la copia que tenga un nodo no cuenta: se lee
/// siempre la del modulo. No estan las que el canvas si cambia por nodo (n,
/// numberOfImages y sceneCount, que son su numero de salidas).
/// </summary>
public static class ModuleConfigKeys
{
    private static readonly HashSet<string> ModuleOwned = new(StringComparer.OrdinalIgnoreCase)
    {
        // Texto, orquestador y coordinador
        "systemPrompt", "temperature", "maxTokens", "reasoningEffort", "webSearch",
        // Imagen
        "imagePrompt", "maxReferenceImages", "size", "quality", "width", "height", "presetStyle",
        // Video
        "videoPrompt", "resolution", "aspectRatio", "duration", "audio", "negativePrompt", "seed",
        "promptEnhance", "frameInterpolation", "videoQuality",
        // Voz (OpenAI y ElevenLabs)
        "voice", "speed", "voiceInstructions", "voiceName", ElevenLabsSpeech.VoiceOwnerKey, "voiceLanguage",
        "voiceAccent", "voiceUseCase", ElevenLabsSpeech.StabilityKey, ElevenLabsSpeech.SimilarityKey,
        ElevenLabsSpeech.StyleKey,
        // Canva, texto fijo, embeddings y transcripcion
        "accessToken", "exportFormat", "brandTemplateId", "designType", "content", "dimensions", "language",
        // Subtitulos
        SubtitleBuilder.WordsKey, SubtitleBuilder.ColorKey, SubtitleBuilder.SizeKey,
        SubtitleBuilder.PositionKey, SubtitleBuilder.UppercaseKey,
        // Search Console
        "siteUrl", "searchType", "dateRange", "startDate", "endDate", "filters", "aggregationType", "dataState",
        "orderBy", "rowLimit", "startRow", "outputFormat", "minImpressions", "minClicks", "minPosition", "maxPosition",
    };

    /// <summary>Si la clave solo se edita en el modulo y la de un nodo no cuenta.</summary>
    public static bool IsModuleOwned(string key) => ModuleOwned.Contains(key);

    /// <summary>
    /// La configuracion de un nodo sin las claves del modulo. null si no queda nada
    /// o no es un objeto JSON (una configuracion rota no se arrastra).
    /// </summary>
    public static string? NodeOnly(string? config)
    {
        if (string.IsNullOrWhiteSpace(config)) return null;
        try
        {
            using var doc = JsonDocument.Parse(config);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            var kept = doc.RootElement.EnumerateObject()
                .Where(p => !IsModuleOwned(p.Name))
                .ToDictionary(p => p.Name, p => p.Value.Clone());
            return kept.Count == 0 ? null : JsonSerializer.Serialize(kept);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
