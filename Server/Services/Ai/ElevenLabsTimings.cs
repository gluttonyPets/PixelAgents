using System.Net.Http.Headers;
using System.Text.Json;

namespace Server.Services.Ai;

/// <summary>
/// En que momento suena cada palabra de un audio, segun ElevenLabs. Es lo unico
/// que el modulo de subtitulos le pide a la IA: el texto lo pinta ffmpeg.
///
/// Dos caminos:
/// - Con el texto de la locucion: <c>POST /v1/forced-alignment</c> lo alinea con
///   el audio. Los subtitulos salen con el texto exacto, sin palabras mal oidas.
/// - Sin texto: <c>POST /v1/speech-to-text</c> (Scribe) lo transcribe con tiempos
///   por palabra.
/// Las dos cuestan lo mismo, por hora de audio (<see cref="PricingCatalog"/>).
///
/// Va aparte del handler, como <see cref="ElevenLabsSpeech"/>, para probar sin red
/// que se manda y como se lee la respuesta.
/// </summary>
public static class ElevenLabsTimings
{
    public const string AlignmentUrl = ElevenLabsSpeech.BaseUrl + "/v1/forced-alignment";
    public const string TranscriptionUrl = ElevenLabsSpeech.BaseUrl + "/v1/speech-to-text";

    /// <summary>Una palabra y cuando suena, en segundos desde el inicio del audio.</summary>
    public record Word(string Text, double Start, double End);

    /// <summary>Formulario de la alineacion: el audio y el texto que se dice en el.</summary>
    public static MultipartFormDataContent BuildAlignmentForm(byte[] audio, string fileName, string text)
    {
        var form = new MultipartFormDataContent();
        form.Add(AudioPart(audio), "file", fileName);
        form.Add(new StringContent(text), "text");
        return form;
    }

    /// <summary>Formulario de la transcripcion con tiempos por palabra.</summary>
    public static MultipartFormDataContent BuildTranscriptionForm(
        byte[] audio, string fileName, string model, string? language)
    {
        var form = new MultipartFormDataContent();
        form.Add(AudioPart(audio), "file", fileName);
        form.Add(new StringContent(model), "model_id");
        form.Add(new StringContent("word"), "timestamps_granularity");
        // Sin esto Scribe marca risas, musica... como eventos, que no son subtitulo.
        form.Add(new StringContent("false"), "tag_audio_events");
        if (!string.IsNullOrWhiteSpace(language))
            form.Add(new StringContent(language.Trim()), "language_code");
        return form;
    }

    /// <summary>
    /// Palabras de la respuesta, de cualquiera de los dos caminos. La transcripcion
    /// mezcla palabras con espacios (<c>type: spacing</c>) y eventos de audio; solo
    /// cuentan las palabras.
    /// </summary>
    public static List<Word> ParseWords(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var words = new List<Word>();
        if (!doc.RootElement.TryGetProperty("words", out var list) || list.ValueKind != JsonValueKind.Array)
            return words;

        foreach (var w in list.EnumerateArray())
        {
            if (w.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() != "word")
                continue;

            var text = w.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()!.Trim()
                : "";
            if (text.Length == 0
                || !w.TryGetProperty("start", out var s) || s.ValueKind != JsonValueKind.Number
                || !w.TryGetProperty("end", out var e) || e.ValueKind != JsonValueKind.Number)
                continue;

            var start = Math.Max(0, s.GetDouble());
            words.Add(new Word(text, start, Math.Max(start, e.GetDouble())));
        }

        return words;
    }

    private static ByteArrayContent AudioPart(byte[] audio)
    {
        var part = new ByteArrayContent(audio);
        part.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        return part;
    }
}
