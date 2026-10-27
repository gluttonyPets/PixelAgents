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

    /// <summary>
    /// Dueno publico de una voz de la biblioteca de ElevenLabs. Solo lo tienen las
    /// voces elegidas de la biblioteca: con el, si la voz aun no esta en la cuenta,
    /// el proveedor la anade antes de locutar (la API no locuta con voces ajenas).
    /// </summary>
    public const string VoiceOwnerKey = "voiceOwner";

    /// <summary>Claves de los metadatos de una locucion que el handler apunta en el log.</summary>
    public const string UsedVoiceKey = "voiceId", VoiceSourceKey = "voiceSource", HistoryItemKey = "historyItemId";

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

    public static string? VoiceOwner(IReadOnlyDictionary<string, object> config) =>
        ReadString(config, VoiceOwnerKey);

    public static string? VoiceName(IReadOnlyDictionary<string, object> config) =>
        ReadString(config, "voiceName");

    /// <summary>URL de la peticion para la voz configurada, con salida mp3.</summary>
    public static string BuildRequestUrl(IReadOnlyDictionary<string, object> config) =>
        BuildRequestUrl(VoiceId(config));

    public static string BuildRequestUrl(string voiceId) =>
        $"{BaseUrl}/v1/text-to-speech/{Uri.EscapeDataString(voiceId)}?output_format=mp3_44100_128";

    /// <summary>URL para anadir a la cuenta una voz de la biblioteca.</summary>
    public static string BuildAddSharedVoiceUrl(string ownerId, string voiceId) =>
        $"{BaseUrl}/v1/voices/add/{Uri.EscapeDataString(ownerId)}/{Uri.EscapeDataString(voiceId)}";

    /// <summary>
    /// Id en la cuenta de una voz de la biblioteca que ya se anadio, buscandola por
    /// el id que tenia en la biblioteca. null si no esta.
    /// </summary>
    public static string? FindAddedVoice(IEnumerable<Voice> account, string libraryVoiceId) =>
        account.FirstOrDefault(v => v.Id == libraryVoiceId || v.OriginalVoiceId == libraryVoiceId)?.Id;

    /// <summary>Tope de una muestra de voz: son unos segundos de mp3, no mas de unos cientos de KB.</summary>
    public const int MaxSampleBytes = 5 * 1024 * 1024;

    /// <summary>
    /// URL de la muestra gratis de una voz (<c>preview_url</c>) que el servidor puede
    /// reenviar. Solo las del almacen publico de ElevenLabs en Google Storage: el
    /// endpoint que las sirve no puede convertirse en un proxy hacia cualquier sitio.
    /// </summary>
    public static bool TryParseSampleUrl(string? url, out Uri sample)
    {
        sample = null!;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            return false;
        if (!uri.Host.Equals("storage.googleapis.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/eleven-", StringComparison.OrdinalIgnoreCase))
            return false;

        sample = uri;
        return true;
    }

    /// <summary>
    /// Tipo con el que se sirve la muestra. Google Storage las entrega como
    /// <c>text/plain</c>: Chrome las reproduce igual, pero Firefox y Safari no.
    /// </summary>
    public static string SampleContentType(Uri sample) =>
        Path.GetExtension(sample.AbsolutePath).ToLowerInvariant() switch
        {
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            _ => "audio/mpeg",
        };

    /// <summary>
    /// La API respondio que la voz no existe para esta cuenta: es lo que pasa con
    /// una voz de la biblioteca que todavia no se ha anadido.
    /// </summary>
    public static bool IsVoiceNotFound(int statusCode, string body) =>
        statusCode is 400 or 404 && body.Contains("voice_not_found", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Busqueda de la biblioteca de voces por idioma (codigo ISO 639-1), las mas
    /// usadas primero: son las que mejor suenan de media. Acento y uso filtran por
    /// el valor exacto que trae la propia biblioteca (ver <see cref="CountFacet"/>).
    /// </summary>
    public static string BuildSharedVoicesUrl(string language, string? accent = null, string? useCase = null, int page = 0)
    {
        var url = $"{BaseUrl}/v1/shared-voices?page_size=100&sort=usage_character_count_1y"
            + $"&language={Uri.EscapeDataString(language)}";
        if (!string.IsNullOrWhiteSpace(accent)) url += "&accent=" + Uri.EscapeDataString(accent.Trim());
        if (!string.IsNullOrWhiteSpace(useCase)) url += "&use_cases=" + Uri.EscapeDataString(useCase.Trim());
        if (page > 0) url += "&page=" + page;
        return url;
    }

    /// <summary>
    /// Valores distintos de un campo (acento, uso) entre las voces, los mas
    /// frecuentes primero. ElevenLabs no publica la lista de valores validos y los
    /// de su web no coinciden con los de la API ("Spanish Castilian" frente a
    /// "peninsular"), asi que se sacan de las voces reales del idioma.
    /// </summary>
    public static List<(string Value, int Count)> CountFacet(IEnumerable<Voice> voices, Func<Voice, string?> field) =>
        voices
            .Select(field)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .GroupBy(v => v!.Trim().ToLowerInvariant())
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(f => f.Item2)
            .ThenBy(f => f.Key)
            .ToList();

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
            ["text"] = WithPauses(model, text),
            ["model_id"] = model,
        };
        if (settings.Count > 0) body["voice_settings"] = settings;

        return JsonSerializer.Serialize(body);
    }

    /// <summary>Pausa entre parrafos: lo que se respira entre dos ideas al narrar.</summary>
    public const string ParagraphPause = "<break time=\"0.5s\" />";

    /// <summary>
    /// ElevenLabs avisa de que muchas pausas en una misma locucion la vuelven
    /// inestable (acelera o mete ruidos); pasado este numero los parrafos se dejan
    /// solo con el salto de linea.
    /// </summary>
    public const int MaxPauses = 8;

    /// <summary>
    /// Modelos que entienden <c>&lt;break time="x.xs" /&gt;</c>. v3 y v4 no: en ellos
    /// la pausa la marcan la puntuacion y el salto de parrafo.
    /// </summary>
    public static bool SupportsBreakTags(string model) => model.ToLowerInvariant() is
        "eleven_multilingual_v2" or "eleven_flash_v2_5" or "eleven_turbo_v2_5" or "eleven_flash_v2" or "eleven_turbo_v2";

    /// <summary>
    /// Texto que se manda: cada salto de parrafo (linea en blanco) lleva una pausa
    /// real en los modelos que la admiten. Sin ella el modelo encadena los parrafos
    /// casi sin respirar y la locucion suena leida.
    /// </summary>
    public static string WithPauses(string model, string text)
    {
        if (!SupportsBreakTags(model)) return text;

        var paragraphs = System.Text.RegularExpressions.Regex.Split(text.Trim(), @"\r?\n\s*\r?\n");
        if (paragraphs.Length < 2) return text;

        var sb = new System.Text.StringBuilder(paragraphs[0].Trim());
        for (var i = 1; i < paragraphs.Length; i++)
        {
            sb.Append(i <= MaxPauses ? $" {ParagraphPause}\n\n" : "\n\n");
            sb.Append(paragraphs[i].Trim());
        }
        return sb.ToString();
    }

    /// <summary>Coste de una locucion: ElevenLabs factura por caracter.</summary>
    public static decimal EstimateCost(string model, int characters)
    {
        if (characters <= 0) return 0m;
        var rate = PricingCatalog.GetAuxiliaryRate(model);
        if (rate is null || !rate.Unit.Equals("1M caracteres", StringComparison.OrdinalIgnoreCase)) return 0m;
        return rate.Amount * characters / 1_000_000m;
    }

    /// <summary>Voz elegible: de la cuenta (sin dueno) o de la biblioteca (con dueno).</summary>
    /// <param name="OriginalVoiceId">
    /// En una voz de la cuenta anadida desde la biblioteca, el id que tenia alli
    /// (<c>sharing.original_voice_id</c>). La copia puede tener otro id, y el modulo
    /// guarda el de la biblioteca.
    /// </param>
    /// <param name="PreviewModel">Modelo con el que se grabo la muestra, si ElevenLabs lo dice.</param>
    public record Voice(string Id, string Label, string? PreviewUrl, string? OwnerId = null,
        string? Accent = null, string? UseCase = null, string? OriginalVoiceId = null, string? PreviewModel = null);

    /// <summary>
    /// Voces de la cuenta (respuesta de <c>GET /v2/voices</c>), con una etiqueta
    /// legible: "Nombre · genero, acento, idioma".
    /// </summary>
    public static List<Voice> ParseVoices(string json, out string? nextPageToken)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        nextPageToken = root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True
            && root.TryGetProperty("next_page_token", out var token) && token.ValueKind == JsonValueKind.String
                ? token.GetString()
                : null;

        var voices = new List<Voice>();
        if (!root.TryGetProperty("voices", out var list) || list.ValueKind != JsonValueKind.Array) return voices;

        foreach (var v in list.EnumerateArray())
        {
            var id = Str(v, "voice_id");
            if (id is null) continue;

            var details = new List<string>();
            if (v.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "gender", "accent", "language" })
                    if (Str(labels, key) is { } label) details.Add(label);
            }
            if (Str(v, "category") is "cloned" or "professional")
                details.Add("clonada");

            var original = v.TryGetProperty("sharing", out var sharing) && sharing.ValueKind == JsonValueKind.Object
                ? Str(sharing, "original_voice_id")
                : null;

            voices.Add(new Voice(id, Label(Str(v, "name") ?? id, details), Str(v, "preview_url"),
                OriginalVoiceId: original != id ? original : null));
        }

        return voices;
    }

    /// <summary>
    /// Voces de la biblioteca (respuesta de <c>GET /v1/shared-voices</c>). Llevan
    /// el dueno publico, que hace falta para anadirlas a la cuenta.
    /// </summary>
    /// <param name="language">
    /// Idioma buscado. La muestra general de una voz puede estar en otro idioma;
    /// la de <c>verified_languages</c> es la de este idioma y dice con que modelo
    /// se grabo, que es como suena la voz que se va a locutar.
    /// </param>
    public static List<Voice> ParseSharedVoices(string json, string? language = null)
    {
        using var doc = JsonDocument.Parse(json);
        var voices = new List<Voice>();
        if (!doc.RootElement.TryGetProperty("voices", out var list) || list.ValueKind != JsonValueKind.Array)
            return voices;

        foreach (var v in list.EnumerateArray())
        {
            var id = Str(v, "voice_id");
            var owner = Str(v, "public_owner_id");
            if (id is null || owner is null) continue;

            var details = new List<string>();
            foreach (var key in new[] { "gender", "age", "accent", "descriptive" })
                if (Str(v, key) is { } value) details.Add(value);

            var verified = VerifiedPreview(v, language);
            voices.Add(new Voice(id, Label(Str(v, "name") ?? id, details),
                verified?.PreviewUrl ?? Str(v, "preview_url"), owner, Str(v, "accent"), Str(v, "use_case"),
                PreviewModel: verified?.Model));
        }

        return voices;
    }

    /// <summary>
    /// Muestra de la voz en ese idioma y modelo con el que se grabo. Si hay varias
    /// (un idioma con dos acentos), la del acento de la voz.
    /// </summary>
    private static (string PreviewUrl, string? Model)? VerifiedPreview(JsonElement voice, string? language)
    {
        if (string.IsNullOrWhiteSpace(language)
            || !voice.TryGetProperty("verified_languages", out var list) || list.ValueKind != JsonValueKind.Array)
            return null;

        var accent = Str(voice, "accent");
        var matches = list.EnumerateArray()
            .Where(l => l.ValueKind == JsonValueKind.Object
                && string.Equals(Str(l, "language"), language.Trim(), StringComparison.OrdinalIgnoreCase)
                && Str(l, "preview_url") is not null)
            .ToList();
        if (matches.Count == 0) return null;

        var best = matches.FirstOrDefault(l => string.Equals(Str(l, "accent"), accent, StringComparison.OrdinalIgnoreCase));
        if (best.ValueKind == JsonValueKind.Undefined) best = matches[0];
        return (Str(best, "preview_url")!, Str(best, "model_id"));
    }

    private static string Label(string name, List<string> details) =>
        details.Count > 0 ? name + " · " + string.Join(", ", details) : name;

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

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
