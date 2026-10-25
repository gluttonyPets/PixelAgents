using Server.Models;

namespace Server.Services.Ai
{
    /// <summary>
    /// Texto a voz con ElevenLabs. Solo hace Audio: una llamada es una locucion y
    /// el reparto en tomas lo hace <see cref="Handlers.AudioModuleHandler"/>, igual
    /// que con OpenAI. Lo que se manda lo arma <see cref="ElevenLabsSpeech"/>.
    /// </summary>
    public class ElevenLabsProvider : IAiProvider
    {
        public string ProviderType => "ElevenLabs";
        public IEnumerable<string> SupportedModuleTypes => new[] { "Audio" };

        public async Task<AiResult> ExecuteAsync(AiExecutionContext context)
        {
            try
            {
                return context.ModuleType switch
                {
                    "Audio" => await GenerateSpeechAsync(context),
                    _ => AiResult.Fail($"ModuleType '{context.ModuleType}' no soportado por ElevenLabs")
                };
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw; // Propagate cancellation so the executor can stop the pipeline cleanly.
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new TransientProviderException($"Error ElevenLabs transitorio: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                return AiResult.Fail($"Error ElevenLabs: {ex.Message}");
            }
        }

        private static async Task<AiResult> GenerateSpeechAsync(AiExecutionContext context)
        {
            var text = context.Input?.Trim() ?? "";
            if (text.Length == 0)
                return AiResult.Fail("Sin texto que locutar");

            // Igual que con OpenAI: recortar cortaria la locucion a mitad de frase.
            var max = ElevenLabsSpeech.MaxInputChars(context.ModelName);
            if (text.Length > max)
                return AiResult.Fail(
                    $"El texto tiene {text.Length} caracteres y {context.ModelName} admite {max} por locucion. "
                    + "Acortalo o reparte el texto en tomas con marcas ===TOMA n===.");

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, timeoutCts.Token);
            var ct = linkedCts.Token;

            var json = ElevenLabsSpeech.BuildRequestJson(context.ModelName, text, context.Configuration);
            var voiceId = ElevenLabsSpeech.VoiceId(context.Configuration);

            using var http = CreateClient(context.ApiKey);
            var (audio, status, error) = await SpeakAsync(http, voiceId, json, ct);

            // Voz de la biblioteca que aun no esta en la cuenta: se anade (ocupa un
            // hueco de voz en ElevenLabs) y se repite. Se hace aqui y no al elegirla
            // para que curiosear voces en el formulario no llene la cuenta.
            var owner = ElevenLabsSpeech.VoiceOwner(context.Configuration);
            if (audio is null && owner is not null && ElevenLabsSpeech.IsVoiceNotFound(status, error))
            {
                var (addedId, addError) = await AddSharedVoiceAsync(
                    http, owner, voiceId, ElevenLabsSpeech.VoiceName(context.Configuration), ct);
                if (addedId is null)
                    return AiResult.Fail("No se pudo anadir la voz de la biblioteca a tu cuenta de ElevenLabs "
                        + $"(quiza no te quedan huecos de voz): {addError}");
                (audio, status, error) = await SpeakAsync(http, addedId, json, ct);
            }

            if (audio is null)
            {
                // 5xx y 429 se relanzan como transitorios para que el executor reintente.
                if (status >= 500 || status == 429)
                    throw new HttpRequestException($"ElevenLabs {status}: {error}");
                return AiResult.Fail($"ElevenLabs ({status}): {error}");
            }

            if (audio.Length == 0)
                return AiResult.Fail("ElevenLabs devolvio un audio vacio");

            var result = AiResult.OkFile(audio, "audio/mpeg", new Dictionary<string, object>
            {
                ["model"] = context.ModelName,
                ["characters"] = text.Length,
            });
            result.EstimatedCost = ElevenLabsSpeech.EstimateCost(context.ModelName, text.Length);
            result.SentPayload = json;
            return result;
        }

        private static async Task<(byte[]? Audio, int Status, string Error)> SpeakAsync(
            HttpClient http, string voiceId, string json, CancellationToken ct)
        {
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(ElevenLabsSpeech.BuildRequestUrl(voiceId), content, ct);
            if (!response.IsSuccessStatusCode)
                return (null, (int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
            return (await response.Content.ReadAsByteArrayAsync(ct), (int)response.StatusCode, "");
        }

        /// <summary>Anade a la cuenta una voz de la biblioteca y devuelve su id en la cuenta.</summary>
        private static async Task<(string? VoiceId, string? Error)> AddSharedVoiceAsync(
            HttpClient http, string ownerId, string voiceId, string? name, CancellationToken ct)
        {
            var body = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["new_name"] = string.IsNullOrWhiteSpace(name) ? voiceId : name,
            });
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(ElevenLabsSpeech.BuildAddSharedVoiceUrl(ownerId, voiceId), content, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) return (null, $"HTTP {(int)response.StatusCode}: {text}");

            using var doc = System.Text.Json.JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("voice_id", out var id) && id.GetString() is { Length: > 0 } added
                ? (added, null)
                : (voiceId, null);
        }

        /// <summary>
        /// Una pagina (100) de voces de la biblioteca de ElevenLabs en un idioma
        /// (codigo ISO 639-1), las mas usadas primero, opcionalmente filtradas por
        /// acento y uso.
        /// </summary>
        public static async Task<List<ElevenLabsSpeech.Voice>> ListSharedVoicesAsync(
            string apiKey, string language, CancellationToken ct,
            string? accent = null, string? useCase = null, int page = 0)
        {
            using var http = CreateClient(apiKey);
            using var response = await http.GetAsync(
                ElevenLabsSpeech.BuildSharedVoicesUrl(language, accent, useCase, page), ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"ElevenLabs ({(int)response.StatusCode}) al buscar en la biblioteca: {body}");
            return ElevenLabsSpeech.ParseSharedVoices(body);
        }

        /// <summary>
        /// Voces que puede usar la cuenta: las predeterminadas, las clonadas y las
        /// guardadas de la biblioteca. Se pagina hasta 500 para no colgar la UI con
        /// una cuenta enorme.
        /// </summary>
        public static async Task<List<ElevenLabsSpeech.Voice>> ListVoicesAsync(string apiKey, CancellationToken ct)
        {
            using var http = CreateClient(apiKey);
            var voices = new List<ElevenLabsSpeech.Voice>();
            string? page = null;

            do
            {
                var url = $"{ElevenLabsSpeech.BaseUrl}/v2/voices?page_size=100&sort=name&sort_direction=asc"
                    + (page is null ? "" : "&next_page_token=" + Uri.EscapeDataString(page));
                using var response = await http.GetAsync(url, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        ? "API Key de ElevenLabs invalida o sin permiso para leer voces"
                        : $"ElevenLabs ({(int)response.StatusCode}): {body}");

                voices.AddRange(ElevenLabsSpeech.ParseVoices(body, out page));
            } while (page is not null && voices.Count < 500);

            return voices;
        }

        public async Task<(bool Valid, string? Error)> ValidateKeyAsync(string apiKey)
        {
            try
            {
                using var http = CreateClient(apiKey);
                using var resp = await http.GetAsync($"{ElevenLabsSpeech.BaseUrl}/v1/models");
                if (resp.IsSuccessStatusCode) return (true, null);
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return (false, "API Key de ElevenLabs invalida o expirada");
                var body = await resp.Content.ReadAsStringAsync();
                return (false, $"Error al validar ElevenLabs (HTTP {(int)resp.StatusCode}): {body}");
            }
            catch (Exception ex)
            {
                return (false, $"Error al validar ElevenLabs: {ex.Message}");
            }
        }

        private static HttpClient CreateClient(string apiKey)
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.Add("xi-api-key", apiKey);
            return http;
        }
    }
}
