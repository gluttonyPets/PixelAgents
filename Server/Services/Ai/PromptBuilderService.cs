using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;

namespace Server.Services.Ai
{
    /// <summary>
    /// Asistente conversacional que ayuda al usuario a construir un buen prompt
    /// para un modulo. Funciona en dos fases: primero genera preguntas de detalle
    /// a medida de la descripcion rapida del usuario y despues compone el prompt
    /// final en una estructura coherente. Reutiliza el mismo patron de ejecucion
    /// que <see cref="PromptPlannerService"/> (registry + API key del tenant).
    /// </summary>
    public interface IPromptBuilderService
    {
        /// <summary>Modelos de texto ofrecidos por el asistente, filtrados por las
        /// API keys realmente configuradas en el tenant.</summary>
        Task<IReadOnlyList<PromptBuilderModelOption>> GetAvailableModelsAsync(
            UserDbContext db, CancellationToken ct = default);

        /// <summary>Genera 3-5 preguntas de detalle a partir de la descripcion rapida.</summary>
        Task<PromptBuilderQuestionsResult> GenerateQuestionsAsync(
            UserDbContext db, string modelName, string targetKind, string description,
            CancellationToken ct = default);

        /// <summary>Compone el prompt final estructurado a partir de la descripcion
        /// y las respuestas a las preguntas de detalle.</summary>
        Task<PromptBuilderComposeResult> ComposeAsync(
            UserDbContext db, string modelName, string targetKind, string description,
            IReadOnlyList<PromptBuilderQa> answers, CancellationToken ct = default);

        /// <summary>Interpreta y mejora una peticion del usuario, la integra en un prompt
        /// ya existente conservando lo que habia, y detecta conflictos con las reglas
        /// globales o incoherencias internas, devolviendolos como avisos.</summary>
        Task<PromptBuilderAddResult> AddAsync(
            UserDbContext db, string modelName, string targetKind, string currentPrompt,
            string addition, CancellationToken ct = default);
    }

    public record PromptBuilderModelOption(string Provider, string ModelName, string DisplayName);
    public record PromptBuilderQa(string Question, string Answer);
    public record PromptBuilderQuestionsResult(bool Success, List<string> Questions, string? Error);
    public record PromptBuilderComposeResult(bool Success, string? Prompt, string? Error);
    public record PromptBuilderAddResult(bool Success, string? Prompt, List<string> Warnings, string? Error);

    public class PromptBuilderService : IPromptBuilderService
    {
        // Lista curada de modelos de texto "buenos por defecto" por proveedor.
        // Solo se muestran los cuyo proveedor tenga API key configurada.
        private static readonly IReadOnlyList<PromptBuilderModelOption> _candidateModels = new List<PromptBuilderModelOption>
        {
            new("OpenAI",    "gpt-5.4",                        "GPT-5.4"),
            new("OpenAI",    "gpt-4o",                         "GPT-4o"),
            new("OpenAI",    "gpt-4o-mini",                    "GPT-4o mini"),
            new("Anthropic", "claude-opus-4-6",                "Claude Opus 4.6"),
            new("Anthropic", "claude-sonnet-4-6",              "Claude Sonnet 4.6"),
            new("Anthropic", "claude-haiku-4-5-20251001",      "Claude Haiku 4.5"),
            new("Google",    "gemini-2.5-pro",                 "Gemini 2.5 Pro"),
            new("Google",    "gemini-2.5-flash",               "Gemini 2.5 Flash"),
            new("xAI",       "grok-4-0709",                    "Grok 4"),
            new("xAI",       "grok-3",                         "Grok 3"),
        };

        private readonly IAiProviderRegistry _registry;
        private readonly ILogger<PromptBuilderService> _log;

        public PromptBuilderService(IAiProviderRegistry registry, ILogger<PromptBuilderService> log)
        {
            _registry = registry;
            _log = log;
        }

        public async Task<IReadOnlyList<PromptBuilderModelOption>> GetAvailableModelsAsync(
            UserDbContext db, CancellationToken ct = default)
        {
            var configuredProviders = await db.ApiKeys
                .Where(k => k.EncryptedKey != null && k.EncryptedKey != "")
                .Select(k => k.ProviderType)
                .Distinct()
                .ToListAsync(ct);

            var set = new HashSet<string>(configuredProviders, StringComparer.OrdinalIgnoreCase);
            return _candidateModels.Where(m => set.Contains(m.Provider)).ToList();
        }

        public async Task<PromptBuilderQuestionsResult> GenerateQuestionsAsync(
            UserDbContext db, string modelName, string targetKind, string description,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(description))
                return new(false, new(), "Describe primero que quieres que haga el modulo");

            var setup = await ResolveModelAsync(db, modelName, ct);
            if (setup.Error is not null) return new(false, new(), setup.Error);

            var prompt = BuildQuestionsPrompt(NormalizeKind(targetKind), description);
            var (ok, raw, err) = await RunAsync(setup.Provider!, setup.ModelName!, setup.ApiKey!, prompt, 1200, ct);
            if (!ok) return new(false, new(), err);

            var questions = ParseStringArray(raw, "questions", max: 6);
            if (questions.Count == 0)
                return new(false, new(), "El modelo no devolvio preguntas validas. Prueba de nuevo o cambia de modelo.");

            return new(true, questions, null);
        }

        public async Task<PromptBuilderComposeResult> ComposeAsync(
            UserDbContext db, string modelName, string targetKind, string description,
            IReadOnlyList<PromptBuilderQa> answers, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(description))
                return new(false, null, "Falta la descripcion inicial");

            var setup = await ResolveModelAsync(db, modelName, ct);
            if (setup.Error is not null) return new(false, null, setup.Error);

            var prompt = BuildComposePrompt(NormalizeKind(targetKind), description, answers);
            var (ok, raw, err) = await RunAsync(setup.Provider!, setup.ModelName!, setup.ApiKey!, prompt, 2500, ct);
            if (!ok) return new(false, null, err);

            var cleaned = StripCodeFences(raw).Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
                return new(false, null, "El modelo no devolvio ningun prompt. Prueba de nuevo.");

            return new(true, cleaned, null);
        }

        public async Task<PromptBuilderAddResult> AddAsync(
            UserDbContext db, string modelName, string targetKind, string currentPrompt,
            string addition, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(addition))
                return new(false, null, new(), "Describe que quieres anadir al prompt");

            var setup = await ResolveModelAsync(db, modelName, ct);
            if (setup.Error is not null) return new(false, null, new(), setup.Error);

            var rulesBlock = await LoadActiveRulesTextAsync(db, ct);
            var prompt = BuildAddPrompt(NormalizeKind(targetKind), currentPrompt ?? "", addition, rulesBlock);
            var (ok, raw, err) = await RunAsync(setup.Provider!, setup.ModelName!, setup.ApiKey!, prompt, 3200, ct);
            if (!ok) return new(false, null, new(), err);

            var (updated, warnings) = ParseAddOutput(raw);
            if (string.IsNullOrWhiteSpace(updated))
                return new(false, null, new(), "El modelo no devolvio ningun prompt. Prueba de nuevo.");

            return new(true, updated, warnings, null);
        }

        private static async Task<string?> LoadActiveRulesTextAsync(UserDbContext db, CancellationToken ct)
        {
            var rules = await db.Rules
                .Where(r => r.IsActive)
                .OrderBy(r => r.SortOrder).ThenBy(r => r.CreatedAt)
                .Select(r => new { r.Title, r.Content })
                .ToListAsync(ct);

            if (rules.Count == 0) return null;

            var sb = new System.Text.StringBuilder();
            foreach (var r in rules)
                sb.AppendLine($"- {r.Title}: {r.Content}");
            return sb.ToString().TrimEnd();
        }

        // ── Helpers de ejecucion ──

        private record ModelSetup(IAiProvider? Provider, string? ModelName, string? ApiKey, string? Error);

        private async Task<ModelSetup> ResolveModelAsync(UserDbContext db, string modelName, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(modelName))
                return new(null, null, null, "Falta indicar el modelo");

            var option = _candidateModels.FirstOrDefault(m =>
                string.Equals(m.ModelName, modelName, StringComparison.OrdinalIgnoreCase));
            if (option is null)
                return new(null, null, null, $"Modelo '{modelName}' no soportado por el asistente");

            var apiKeyEntity = await db.ApiKeys
                .Where(k => k.ProviderType == option.Provider)
                .OrderBy(k => k.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (apiKeyEntity is null || string.IsNullOrEmpty(apiKeyEntity.EncryptedKey))
                return new(null, null, null,
                    $"No hay API Key de {option.Provider} configurada. Anade una en Configuracion > API Keys.");

            var provider = _registry.GetProvider(option.Provider);
            if (provider is null)
                return new(null, null, null, $"Proveedor '{option.Provider}' no disponible");

            return new(provider, option.ModelName, apiKeyEntity.EncryptedKey, null);
        }

        private async Task<(bool Ok, string Raw, string? Error)> RunAsync(
            IAiProvider provider, string modelName, string apiKey, string input, int maxTokens, CancellationToken ct)
        {
            var aiContext = new AiExecutionContext
            {
                ModuleType = "Text",
                ModelName = modelName,
                ApiKey = apiKey,
                Input = input,
                Configuration = new() { ["maxTokens"] = maxTokens },
                CancellationToken = ct,
            };

            try
            {
                var result = await provider.ExecuteAsync(aiContext);
                if (!result.Success)
                    return (false, "", result.Error ?? "Error al llamar a la IA");
                return (true, result.TextOutput ?? "", null);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "PromptBuilderService: fallo llamando al modelo {Model}", modelName);
                return (false, "", ex.Message);
            }
        }

        private static string NormalizeKind(string? kind) =>
            string.Equals(kind, "image", StringComparison.OrdinalIgnoreCase) ? "image" : "text";

        // ── Prompts que guian al modelo ──

        private static string BuildQuestionsPrompt(string kind, string description)
        {
            var target = kind == "image"
                ? "un prompt para un modelo de generacion de IMAGENES"
                : "un prompt (system prompt) para un modelo de TEXTO";

            return
$@"Eres un experto en ingenieria de prompts. Tu tarea NO es escribir el prompt todavia,
sino ayudar al usuario a precisarlo. El usuario quiere construir {target} y ha dado
esta descripcion rapida:

""{description}""

Genera entre 3 y 5 preguntas breves, concretas y utiles que, al responderlas, permitan
escribir un prompt mucho mejor y mas preciso. Enfocate en lo que falta por concretar:
objetivo, publico, tono/estilo, formato de salida, restricciones, datos clave o ejemplos.
No hagas preguntas genericas ni obvias; adaptalas a esta descripcion en concreto.
Escribe las preguntas en espanol claro.

Devuelve EXCLUSIVAMENTE un JSON valido con este formato exacto, sin texto adicional,
sin comentarios y sin markdown:
{{
  ""questions"": [
    ""primera pregunta"",
    ""segunda pregunta""
  ]
}}";
        }

        private static string BuildComposePrompt(string kind, string description, IReadOnlyList<PromptBuilderQa> answers)
        {
            var qa = answers is { Count: > 0 }
                ? string.Join("\n", answers
                    .Where(a => !string.IsNullOrWhiteSpace(a.Answer))
                    .Select(a => $"- {a.Question.Trim()}\n  Respuesta: {a.Answer.Trim()}"))
                : "(el usuario no aporto detalles adicionales)";

            if (kind == "image")
            {
                return
$@"Eres director de arte y experto en prompts para modelos de generacion de imagenes.
Con la siguiente informacion, redacta UN prompt de imagen listo para pegar en la
herramienta de generacion.

Descripcion inicial del usuario:
""{description}""

Detalles aportados:
{qa}

Requisitos del prompt que debes escribir:
- En espanol, claro y concreto.
- Describe sujeto, composicion, estilo visual, iluminacion, paleta de color, formato/encuadre
  y cualquier texto que deba aparecer en la imagen (si lo hay, con ortografia perfecta).
- Nada de teoria ni explicaciones: solo el prompt final, autocontenido.

Responde UNICAMENTE con el texto del prompt final, sin comillas, sin markdown,
sin encabezados como 'Prompt:' ni comentarios previos o posteriores.";
            }

            return
$@"Eres un experto en ingenieria de prompts. Con la siguiente informacion, redacta UN
system prompt de alta calidad, listo para pegar en la configuracion de un modulo de IA.

Descripcion inicial del usuario:
""{description}""

Detalles aportados:
{qa}

Estructura el prompt de forma coherente y escaneable, usando encabezados en mayusculas
solo cuando aporten claridad (por ejemplo ROL, TAREA, DETALLES, FORMATO DE SALIDA,
RESTRICCIONES). Adapta las secciones a lo que realmente aplique; no fuerces secciones
vacias. Escribe en espanol, con instrucciones especificas y accionables, sin relleno.

Responde UNICAMENTE con el texto del prompt final, sin markdown de bloque de codigo,
sin encabezados como 'Prompt:' ni comentarios previos o posteriores.";
        }

        private static string BuildAddPrompt(string kind, string current, string addition, string? rulesBlock)
        {
            var kindWord = kind == "image" ? "prompt de imagen" : "system prompt";
            var currentBlock = string.IsNullOrWhiteSpace(current)
                ? "(el prompt actual esta vacio)"
                : current;
            var rulesSection = string.IsNullOrWhiteSpace(rulesBlock)
                ? "(no hay reglas globales configuradas)"
                : rulesBlock;

            return
$@"Eres un experto en ingenieria de prompts. Tienes un {kindWord} ya existente y el usuario
quiere incorporarle algo. Tu trabajo NO es pegar su peticion tal cual: interpretala, entiende
su intencion real, MEJORALA y redactala con claridad, e integrala en el prompt de forma
coherente. Conserva todo lo que ya funciona y respeta la estructura y los encabezados
existentes; anade o ajusta solo lo necesario. No elimines contenido salvo que la peticion lo
contradiga.

Antes de dar el resultado, COMPRUEBA y detecta problemas:
1. Si la peticion (o tu integracion) entra en conflicto con las REGLAS GLOBALES de abajo.
2. Si entra en conflicto, se solapa o contradice algo que ya dice el prompt actual.
3. Si genera alguna incoherencia interna (numeros que no cuadran, instrucciones opuestas,
   promesas que el resto del prompt no puede cumplir, formato contradictorio, etc.).

--- REGLAS GLOBALES ---
{rulesSection}
--- FIN REGLAS GLOBALES ---

--- PROMPT ACTUAL ---
{currentBlock}
--- FIN DEL PROMPT ACTUAL ---

Lo que el usuario quiere anadir (interpretalo y mejoralo, no lo copies literal):
{addition}

Responde EXACTAMENTE en dos secciones, con estos separadores literales y nada mas fuera de ellas:

===PROMPT===
(aqui el prompt COMPLETO actualizado, en espanol, sin markdown de bloque de codigo)

===AVISOS===
(una linea por cada conflicto o incoherencia detectada, empezando con ""- "". Explica el
problema brevemente y, si aplica, como lo has resuelto. Si no hay ningun problema, escribe
exactamente NINGUNO)";
        }

        private static (string Prompt, List<string> Warnings) ParseAddOutput(string raw)
        {
            var text = StripCodeFences(raw ?? "").Trim();
            var warnings = new List<string>();

            const string pMark = "===PROMPT===";
            const string wMark = "===AVISOS===";
            int pi = text.IndexOf(pMark, StringComparison.OrdinalIgnoreCase);
            int wi = text.IndexOf(wMark, StringComparison.OrdinalIgnoreCase);

            string promptPart;
            string warnPart = "";
            if (pi >= 0 && wi > pi)
            {
                promptPart = text.Substring(pi + pMark.Length, wi - (pi + pMark.Length)).Trim();
                warnPart = text[(wi + wMark.Length)..].Trim();
            }
            else if (pi >= 0)
            {
                promptPart = text[(pi + pMark.Length)..].Trim();
            }
            else
            {
                // Sin marcadores: tratamos todo como el prompt (a menos que solo haya avisos).
                if (wi >= 0)
                {
                    promptPart = text[..wi].Trim();
                    warnPart = text[(wi + wMark.Length)..].Trim();
                }
                else
                {
                    promptPart = text;
                }
            }

            if (!string.IsNullOrWhiteSpace(warnPart))
            {
                foreach (var line in warnPart.Replace("\r\n", "\n").Split('\n'))
                {
                    var t = line.Trim().TrimStart('-', '*', '•', ' ', '\t').Trim();
                    if (t.Length == 0) continue;
                    if (string.Equals(t, "NINGUNO", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(t, "NINGUNA", StringComparison.OrdinalIgnoreCase)) continue;
                    warnings.Add(t);
                }
            }

            return (promptPart, warnings);
        }

        // ── Parseo ──

        private static List<string> ParseStringArray(string raw, string property, int max)
        {
            var json = ExtractJsonObject(raw);
            if (json is null) return new();

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return new();

                var list = new List<string>();
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var v = item.GetString();
                        if (!string.IsNullOrWhiteSpace(v)) list.Add(v.Trim());
                    }
                }
                if (list.Count > max) list = list.Take(max).ToList();
                return list;
            }
            catch
            {
                return new();
            }
        }

        private static string? ExtractJsonObject(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var cleaned = StripCodeFences(raw);
            var start = cleaned.IndexOf('{');
            var end = cleaned.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            return cleaned.Substring(start, end - start + 1);
        }

        private static string StripCodeFences(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var firstNewline = cleaned.IndexOf('\n');
                if (firstNewline >= 0) cleaned = cleaned[(firstNewline + 1)..];
                var fenceEnd = cleaned.LastIndexOf("```", StringComparison.Ordinal);
                if (fenceEnd >= 0) cleaned = cleaned[..fenceEnd];
                cleaned = cleaned.Trim();
            }
            return cleaned;
        }
    }
}
