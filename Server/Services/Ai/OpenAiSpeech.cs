using System.Globalization;
using System.Text.Json;

namespace Server.Services.Ai;

/// <summary>
/// Peticion de texto a voz de OpenAI (<c>POST /v1/audio/speech</c>).
///
/// Va aparte del proveedor para poder probar sin red lo que de verdad se rompe:
/// que parametro se manda a que modelo. Los dos TTS de OpenAI no aceptan lo
/// mismo:
///   - <c>tts-1</c> / <c>tts-1-hd</c> aceptan <c>speed</c> pero no
///     <c>instructions</c>;
///   - <c>gpt-4o-mini-tts</c> acepta <c>instructions</c> (tono, acento, ritmo)
///     y no hace caso de <c>speed</c>. Para no perder la velocidad que el usuario
///     configuro, en ese modelo se traduce a una indicacion de ritmo dentro de
///     las instrucciones.
/// </summary>
public static class OpenAiSpeech
{
    /// <summary>Maximo de caracteres que admite la API en una sola locucion.</summary>
    public const int MaxInputChars = 4096;

    public const string DefaultVoice = "alloy";

    /// <summary>Clave de configuracion con las indicaciones de tono para gpt-4o-mini-tts.</summary>
    public const string InstructionsKey = "voiceInstructions";

    /// <summary>
    /// Caracteres por minuto de locucion con los que se estima la duracion del
    /// audio cuando el modelo factura por tokens de audio y la respuesta no trae
    /// uso: unos 15 caracteres por segundo en una voz en off en castellano.
    /// </summary>
    private const double CharsPerMinute = 900d;

    /// <summary>Tokens de audio por minuto de gpt-4o-mini-tts (OpenAI lo publica como ~$0.015/min a $12/1M).</summary>
    private const double AudioTokensPerMinute = 1250d;

    public static bool AcceptsInstructions(string model) =>
        model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Cuerpo JSON de la peticion para <paramref name="text"/>.</summary>
    public static string BuildRequestJson(string model, string text, IReadOnlyDictionary<string, object> config)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["input"] = text,
            ["voice"] = ReadString(config, "voice") ?? DefaultVoice,
            ["response_format"] = "mp3",
        };

        var speed = ReadSpeed(config);

        if (AcceptsInstructions(model))
        {
            var instructions = BuildInstructions(ReadString(config, InstructionsKey), speed);
            if (instructions is not null) body["instructions"] = instructions;
        }
        else if (speed is double s)
        {
            body["speed"] = s;
        }

        return JsonSerializer.Serialize(body);
    }

    /// <summary>
    /// Coste estimado de una locucion. tts-1 factura por caracter, asi que es
    /// exacto; gpt-4o-mini-tts factura tokens de audio que la respuesta no
    /// devuelve, asi que se estima la duracion por caracteres y se suma el texto
    /// de entrada (~4 caracteres por token).
    /// </summary>
    public static decimal EstimateCost(string model, int characters)
    {
        if (characters <= 0) return 0m;

        var rate = PricingCatalog.GetAuxiliaryRate(model);
        if (rate is null) return 0m;

        if (rate.Unit.Equals("1M caracteres", StringComparison.OrdinalIgnoreCase))
            return rate.Amount * characters / 1_000_000m;

        if (rate.Unit.Equals("1M tokens de audio", StringComparison.OrdinalIgnoreCase))
        {
            var audioTokens = (decimal)(characters / CharsPerMinute * AudioTokensPerMinute);
            var textTokens = characters / 4m;
            return rate.Amount * audioTokens / 1_000_000m + TextInputUsdPerMillion * textTokens / 1_000_000m;
        }

        return 0m;
    }

    /// <summary>Precio del texto de entrada de gpt-4o-mini-tts (la nota de su tarifa en el catalogo).</summary>
    private const decimal TextInputUsdPerMillion = 0.60m;

    private static string? BuildInstructions(string? configured, double? speed)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(configured)) parts.Add(configured.Trim());

        if (speed is double s)
        {
            var pace = s > 1
                ? $"Habla mas rapido de lo normal, a unas {s.ToString("0.##", CultureInfo.InvariantCulture)} veces la velocidad habitual."
                : $"Habla mas despacio de lo normal, a unas {s.ToString("0.##", CultureInfo.InvariantCulture)} veces la velocidad habitual.";
            parts.Add(pace);
        }

        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    /// <summary>Velocidad configurada si difiere de la normal, acotada al rango de la API.</summary>
    private static double? ReadSpeed(IReadOnlyDictionary<string, object> config)
    {
        if (!config.TryGetValue("speed", out var raw)) return null;

        double? value = raw switch
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

        if (value is not double v || Math.Abs(v - 1d) < 0.001) return null;
        return Math.Clamp(v, 0.25, 4.0);
    }

    private static string? ReadString(IReadOnlyDictionary<string, object> config, string key)
    {
        if (!config.TryGetValue(key, out var raw)) return null;
        var value = raw is JsonElement { ValueKind: JsonValueKind.String } je ? je.GetString() : raw as string;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
