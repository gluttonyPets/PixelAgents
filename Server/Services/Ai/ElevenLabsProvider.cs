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

            using var http = CreateClient(context.ApiKey);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(ElevenLabsSpeech.BuildRequestUrl(context.Configuration), content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                // 5xx y 429 se relanzan como transitorios para que el executor reintente.
                if ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429)
                    throw new HttpRequestException($"ElevenLabs {(int)response.StatusCode}: {error}");
                return AiResult.Fail($"ElevenLabs ({(int)response.StatusCode}): {error}");
            }

            var audio = await response.Content.ReadAsByteArrayAsync(ct);
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

        /// <summary>
        /// Voces que puede usar la cuenta: las predeterminadas, las clonadas y las
        /// guardadas de la biblioteca. Se pagina hasta 500 para no colgar la UI con
        /// una cuenta enorme.
        /// </summary>
        public static async Task<List<(string Id, string Label)>> ListVoicesAsync(string apiKey, CancellationToken ct)
        {
            using var http = CreateClient(apiKey);
            var voices = new List<(string Id, string Label)>();
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
