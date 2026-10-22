using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Server.Services.Ai;

/// <summary>
/// Transcripcion de OpenAI (<c>POST /v1/audio/transcriptions</c>).
///
/// Va aparte del proveedor, como <see cref="OpenAiSpeech"/>, para poder probar
/// sin red lo que se rompe: que formulario se manda y como se lee la respuesta.
///
/// Con marcas de tiempo (<see cref="TimestampsKey"/>) se pide
/// <c>verbose_json</c> con granularidad de frase y la salida es una frase por
/// linea, <c>[00:04.2 - 00:09.8] texto</c>. Es el formato que lee el orquestador
/// loop (<see cref="LoopPlanner.ParseTimestamps"/>) para darle a cada parte el
/// texto exacto que suena en su tramo. No todos los modelos de transcripcion
/// admiten <c>verbose_json</c>: si la API lo rechaza se repite sin marcas y el
/// orquestador reparte el texto por proporcion.
/// </summary>
public static class OpenAiTranscription
{
    /// <summary>Casilla del nodo que pide marcas de tiempo.</summary>
    public const string TimestampsKey = "timestamps";

    /// <summary>Clave interna con la duracion del audio, para estimar el coste.</summary>
    public const string AudioSecondsKey = "audioSeconds";

    /// <summary>Limite de la API por fichero.</summary>
    public const int MaxFileBytes = 25 * 1024 * 1024;

    /// <summary>Formulario de la peticion.</summary>
    public static MultipartFormDataContent BuildForm(
        string model, byte[] audio, string fileName, string contentType, bool timestamps, string? language)
    {
        var form = new MultipartFormDataContent();

        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(contentType) ? "audio/mpeg" : contentType);
        form.Add(file, "file", string.IsNullOrWhiteSpace(fileName) ? "audio.mp3" : fileName);

        form.Add(new StringContent(model), "model");
        form.Add(new StringContent(timestamps ? "verbose_json" : "json"), "response_format");
        if (timestamps)
            form.Add(new StringContent("segment"), "timestamp_granularities[]");
        if (!string.IsNullOrWhiteSpace(language))
            form.Add(new StringContent(language.Trim()), "language");

        return form;
    }

    /// <summary>
    /// Texto de la respuesta. En <c>verbose_json</c> con frases se escribe una por
    /// linea con sus marcas; si no trae frases, o es <c>json</c>, el texto plano.
    /// </summary>
    public static string ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var segment in segments.EnumerateArray())
            {
                if (!segment.TryGetProperty("start", out var start) || !start.TryGetDouble(out var s)) continue;
                if (!segment.TryGetProperty("end", out var end) || !end.TryGetDouble(out var e)) continue;
                var text = segment.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (sb.Length > 0) sb.Append('\n');
                sb.Append(CultureInfo.InvariantCulture,
                    $"[{LoopPlanner.FormatTime(s)} - {LoopPlanner.FormatTime(e)}] {text}");
            }

            if (sb.Length > 0) return sb.ToString();
        }

        return root.TryGetProperty("text", out var plain) ? plain.GetString()?.Trim() ?? "" : "";
    }

    /// <summary>Duracion que declara la respuesta <c>verbose_json</c>, si la trae.</summary>
    public static double? ReadDuration(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("duration", out var d) && d.TryGetDouble(out var seconds)
                ? seconds
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// ¿Rechaza la API el formato con marcas de tiempo para este modelo? Se mira
    /// el mensaje de error porque el 400 es el mismo que para cualquier otro
    /// parametro mal puesto.
    /// </summary>
    public static bool IsUnsupportedFormatError(string error) =>
        error.Contains("response_format", StringComparison.OrdinalIgnoreCase)
        || error.Contains("verbose_json", StringComparison.OrdinalIgnoreCase)
        || error.Contains("timestamp_granularities", StringComparison.OrdinalIgnoreCase);

    /// <summary>Coste por minuto de audio segun el catalogo de tarifas.</summary>
    public static decimal EstimateCost(string model, double? seconds)
    {
        if (seconds is not double s || s <= 0) return 0m;

        var rate = PricingCatalog.GetAuxiliaryRate(model);
        if (rate is null || !rate.Unit.Equals("minuto", StringComparison.OrdinalIgnoreCase)) return 0m;

        return rate.Amount * (decimal)(s / 60d);
    }
}
