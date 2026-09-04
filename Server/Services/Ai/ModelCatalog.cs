namespace Server.Services.Ai;

/// <summary>
/// Server-side mirror of the model catalog displayed in the Blazor client
/// (Client/Pages/Modules.razor: AllModels). Used by features that need to
/// offer the same list of models that the user sees when creating a module
/// without depending on which AiModules they happen to have saved.
///
/// Keep in sync with the client catalog. When a new model is added in the
/// Razor page, mirror it here.
///
/// Los modelos no se borran nunca de este catálogo, ni siquiera cuando el
/// proveedor los retira: su estado vive en <see cref="ModelLifecycle"/> y la UI
/// lo señala. Borrarlos dejaría módulos guardados apuntando a un id que no
/// aparece en ninguna lista, sin forma de saber qué pasó.
/// </summary>
public static class ModelCatalog
{
    /// <param name="Capabilities">
    /// Etiquetas de lo que sabe hacer el modelo ("text", "vision", "reasoning",
    /// "streaming", "image-generation"...). La pantalla de modelos las cruza con el
    /// precio para responder a "cuanto cuesta lo que necesito", no solo "cuanto cuesta".
    /// </param>
    /// <param name="ContextTokens">
    /// Ventana de contexto en tokens, o null en los modelos donde no aplica (imagen,
    /// audio, diseno). Es la unica medida de capacidad que publican todos los
    /// proveedores con el mismo significado, asi que es la que se puede comparar.
    /// </param>
    /// <param name="PromptChars">
    /// Longitud maxima del prompt en CARACTERES que acepta la API del modelo. Es el
    /// limite contra el que se recorta antes de llamar (<see cref="InputAdapter"/>),
    /// y por eso vive aqui y no repartido por los providers: un limite mal puesto se
    /// come el final del prompt sin que nadie lo vea. Aplica sobre todo a imagen,
    /// donde el prompt viaja como un unico campo de texto con tope propio y no como
    /// mensajes contados en tokens. null = sin limite declarado (se usa el fallback
    /// por familia de <see cref="InputAdapter.GetMaxPromptLength"/>).
    /// </param>
    public record CatalogModel(
        string Id, string DisplayName, string Provider, string[] Types,
        string[]? Capabilities = null, int? ContextTokens = null, int? PromptChars = null);

    public static readonly CatalogModel[] AllModels =
    [
        // ─── OpenAI: Text ───
        new("gpt-5.6-sol",      "GPT-5.6 Sol",      "OpenAI", ["Text","Orchestrator","Coordinator"],
            ["text","vision","reasoning","streaming"], 1_000_000),
        new("gpt-5.6-terra",    "GPT-5.6 Terra",    "OpenAI", ["Text","Orchestrator","Coordinator"],
            ["text","vision","reasoning","streaming"], 1_000_000),
        new("gpt-5.6-luna",     "GPT-5.6 Luna",     "OpenAI", ["Text","Orchestrator","Coordinator"],
            ["text","vision","reasoning","streaming"], 1_000_000),
        new("gpt-5.5",          "GPT-5.5",          "OpenAI", ["Text","Orchestrator","Coordinator"],
            ["text","vision","reasoning","streaming"], 1_000_000),
        new("gpt-5.5-pro",      "GPT-5.5 Pro",      "OpenAI", ["Text"],
            ["text","vision","reasoning","streaming"], 1_000_000),
        new("gpt-5.4",          "GPT-5.4",          "OpenAI", ["Text","Orchestrator","Coordinator"],
            ["text","vision","reasoning","streaming"], 1_000_000),
        new("gpt-5.4-pro",      "GPT-5.4 Pro",      "OpenAI", ["Text"],
            ["text","vision","reasoning","streaming"], 1_000_000),
        new("gpt-5.4-mini",     "GPT-5.4 Mini",     "OpenAI", ["Text","Orchestrator","Coordinator"],
            ["text","vision","reasoning","streaming"], 1_000_000),
        new("gpt-5.4-nano",     "GPT-5.4 Nano",     "OpenAI", ["Text"],
            ["text","vision","reasoning","streaming"], 1_000_000),
        new("gpt-5.3",          "GPT-5.3",          "OpenAI", ["Text"],
            ["text","vision","reasoning","streaming"], 400_000),
        new("gpt-5.2",          "GPT-5.2",          "OpenAI", ["Text"],
            ["text","vision","reasoning","streaming"], 400_000),
        new("gpt-5.1",          "GPT-5.1",          "OpenAI", ["Text"],
            ["text","vision","reasoning","streaming"], 400_000),
        new("gpt-5",            "GPT-5",            "OpenAI", ["Text"],
            ["text","vision","reasoning","streaming"], 400_000),
        new("gpt-5-mini",       "GPT-5 Mini",       "OpenAI", ["Text"],
            ["text","vision","reasoning","streaming"], 400_000),
        new("gpt-5-nano",       "GPT-5 Nano",       "OpenAI", ["Text"],
            ["text","vision","reasoning","streaming"], 400_000),
        new("gpt-4.1",          "GPT-4.1",          "OpenAI", ["Text"],
            ["text","vision","streaming"], 1_000_000),
        new("gpt-4.1-mini",     "GPT-4.1 Mini",     "OpenAI", ["Text"],
            ["text","vision","streaming"], 1_000_000),
        new("gpt-4.1-nano",     "GPT-4.1 Nano",     "OpenAI", ["Text"],
            ["text","vision","streaming"], 1_000_000),
        new("gpt-4o",           "GPT-4o",           "OpenAI", ["Text","Orchestrator","Coordinator"],
            ["text","vision","streaming"], 128_000),
        new("gpt-4o-mini",      "GPT-4o Mini",      "OpenAI", ["Text","Orchestrator","Coordinator"],
            ["text","vision","streaming"], 128_000),
        new("o3",               "o3",               "OpenAI", ["Text"],
            ["text","vision","reasoning","streaming"], 200_000),
        new("o3-mini",          "o3 Mini",          "OpenAI", ["Text"],
            ["text","reasoning","streaming"], 200_000),
        new("o4-mini",          "o4 Mini",          "OpenAI", ["Text"],
            ["text","vision","reasoning","streaming"], 200_000),

        // ─── OpenAI: Image ───
        new("gpt-image-2",      "GPT Image 2",      "OpenAI", ["Image"],
            ["image-generation","image-edit"], null, PromptChars: 32_000),
        new("gpt-image-1.5",    "GPT Image 1.5",    "OpenAI", ["Image"],
            ["image-generation","image-edit"], null, PromptChars: 32_000),
        new("gpt-image-1",      "GPT Image 1",      "OpenAI", ["Image"],
            ["image-generation","image-edit"], null, PromptChars: 32_000),
        new("gpt-image-1-mini", "GPT Image 1 Mini", "OpenAI", ["Image"],
            ["image-generation","image-edit"], null, PromptChars: 32_000),
        new("dall-e-3",         "DALL-E 3",         "OpenAI", ["Image"],
            ["image-generation"], null, PromptChars: 4_000),
        new("dall-e-2",         "DALL-E 2",         "OpenAI", ["Image"],
            ["image-generation","image-editing"], null, PromptChars: 1_000),

        // ─── OpenAI: Embeddings ───
        new("text-embedding-3-large", "Embedding 3 Large", "OpenAI", ["Embeddings"],
            ["embeddings"], 8_192),
        new("text-embedding-3-small", "Embedding 3 Small", "OpenAI", ["Embeddings"],
            ["embeddings"], 8_192),

        // ─── OpenAI: Audio (TTS) ───
        new("gpt-4o-mini-tts",  "GPT-4o Mini TTS",  "OpenAI", ["Audio"],
            ["text-to-speech"], null),
        new("tts-1",            "TTS-1",            "OpenAI", ["Audio"],
            ["text-to-speech"], null),
        new("tts-1-hd",         "TTS-1 HD",         "OpenAI", ["Audio"],
            ["text-to-speech"], null),

        // ─── OpenAI: Transcripción (STT) ───
        new("gpt-transcribe",         "GPT Transcribe",         "OpenAI", ["Transcription"],
            ["speech-to-text"], null),
        new("gpt-4o-transcribe",      "GPT-4o Transcribe",      "OpenAI", ["Transcription"],
            ["speech-to-text"], null),
        new("gpt-4o-mini-transcribe", "GPT-4o Mini Transcribe", "OpenAI", ["Transcription"],
            ["speech-to-text"], null),
        new("whisper-1",              "Whisper",                "OpenAI", ["Transcription"],
            ["speech-to-text"], null),

        // ─── Canva: Diseño ───
        new("canva-design",   "Canva Diseño",   "Canva", ["Design"],
            ["design-creation","export"], null),
        new("canva-autofill", "Canva Autofill", "Canva", ["Design"],
            ["design-creation","autofill","export"], null),

        // ─── Anthropic: Text ───
        new("claude-opus-4-6",             "Claude Opus 4.6",   "Anthropic", ["Text"],
            ["text","vision","streaming"], 200_000),
        new("claude-sonnet-4-6",           "Claude Sonnet 4.6", "Anthropic", ["Text","Orchestrator","Coordinator"],
            ["text","vision","streaming"], 200_000),
        new("claude-opus-4-5-20251124",    "Claude Opus 4.5",   "Anthropic", ["Text"],
            ["text","vision","streaming"], 200_000),
        new("claude-sonnet-4-5-20250929",  "Claude Sonnet 4.5", "Anthropic", ["Text"],
            ["text","vision","streaming"], 200_000),
        new("claude-haiku-4-5-20251001",   "Claude Haiku 4.5",  "Anthropic", ["Text","Orchestrator","Coordinator"],
            ["text","vision","streaming"], 200_000),
        new("claude-opus-4-1-20250805",    "Claude Opus 4.1",   "Anthropic", ["Text"],
            ["text","vision","streaming"], 200_000),
        new("claude-sonnet-4-20250514",    "Claude Sonnet 4",   "Anthropic", ["Text"],
            ["text","vision","streaming"], 200_000),
        new("claude-3-5-haiku-20241022",   "Claude 3.5 Haiku",  "Anthropic", ["Text"],
            ["text","vision","streaming"], 200_000),

        // ─── xAI: Text ───
        new("grok-4-0709",                "Grok 4",                    "xAI", ["Text","Orchestrator","Coordinator"],
            ["text","vision","reasoning","streaming"], 256_000),
        new("grok-4-1-fast-reasoning",    "Grok 4.1 Fast (Reasoning)", "xAI", ["Text","Orchestrator","Coordinator"],
            ["text","vision","reasoning","streaming"], 2_000_000),
        new("grok-4-1-fast-non-reasoning","Grok 4.1 Fast",             "xAI", ["Text","Orchestrator","Coordinator"],
            ["text","vision","streaming"], 2_000_000),
        new("grok-code-fast-1",           "Grok Code Fast",            "xAI", ["Text"],
            ["text","reasoning","streaming"], 256_000),
        new("grok-3",                     "Grok 3",                    "xAI", ["Text","Orchestrator","Coordinator"],
            ["text","vision","reasoning","streaming"], 131_072),
        new("grok-3-fast",                "Grok 3 Fast",               "xAI", ["Text","Orchestrator","Coordinator"],
            ["text","vision","reasoning","streaming"], 131_072),
        new("grok-3-mini",                "Grok 3 Mini",               "xAI", ["Text","Orchestrator","Coordinator"],
            ["text","reasoning","streaming"], 131_072),
        new("grok-3-mini-fast",           "Grok 3 Mini Fast",          "xAI", ["Text","Orchestrator","Coordinator"],
            ["text","reasoning","streaming"], 131_072),
        new("grok-2",                     "Grok 2",                    "xAI", ["Text"],
            ["text","vision","streaming"], 131_072),
        new("grok-2-vision",              "Grok 2 Vision",             "xAI", ["Text"],
            ["text","vision","streaming"], 131_072),

        // ─── xAI: Image ───
        new("grok-imagine-image",     "Grok Imagine",     "xAI", ["Image"],
            ["image-generation"], null, PromptChars: 4_000),
        new("grok-imagine-image-pro", "Grok Imagine Pro", "xAI", ["Image"],
            ["image-generation"], null, PromptChars: 4_000),

        // ─── Google: Text ───
        new("gemini-2.5-flash",     "Gemini 2.5 Flash",      "Google", ["Text","Orchestrator","Coordinator"],
            ["text","vision","streaming"], 1_000_000),
        new("gemini-2.5-pro",       "Gemini 2.5 Pro",        "Google", ["Text"],
            ["text","vision","streaming"], 1_000_000),
        new("gemini-2.0-flash-lite","Gemini 2.0 Flash Lite", "Google", ["Text"],
            ["text","vision","streaming"], 1_000_000),
        new("gemini-1.5-pro",       "Gemini 1.5 Pro",        "Google", ["Text"],
            ["text","vision","streaming"], 2_000_000),
        new("gemini-1.5-flash",     "Gemini 1.5 Flash",      "Google", ["Text"],
            ["text","vision","streaming"], 1_000_000),

        // ─── Google: Image ───
        new("gemini-2.5-flash-image",         "Gemini 2.5 Flash Image",         "Google", ["Image"],
            ["image-generation"], null, PromptChars: 4_000),
        new("gemini-3.1-flash-image-preview", "Gemini 3.1 Flash Image Preview", "Google", ["Image"],
            ["image-generation"], null, PromptChars: 4_000),
        new("gemini-3-pro-image-preview",     "Gemini 3 Pro Image Preview",     "Google", ["Image"],
            ["image-generation"], null, PromptChars: 4_000),

        // ─── Leonardo AI: Image ───
        new("leonardo-phoenix",       "Leonardo Phoenix 1.0",  "LeonardoAI", ["Image"],
            ["image-generation"], null, PromptChars: 1_500),
        new("leonardo-phoenix-0.9",   "Leonardo Phoenix 0.9",  "LeonardoAI", ["Image"],
            ["image-generation"], null, PromptChars: 1_500),
        new("leonardo-flux-dev",      "Leonardo Flux Dev",     "LeonardoAI", ["Image"],
            ["image-generation"], null, PromptChars: 1_500),
        new("leonardo-flux-schnell",  "Leonardo Flux Schnell", "LeonardoAI", ["Image"],
            ["image-generation"], null, PromptChars: 1_500),
    ];

    /// <summary>Datos de catalogo de un modelo por su id, o null si no esta.</summary>
    public static CatalogModel? Find(string? modelId) =>
        string.IsNullOrWhiteSpace(modelId)
            ? null
            : AllModels.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Models matching the given ModuleType ("Image", "Text", ...).</summary>
    public static IEnumerable<CatalogModel> GetByModuleType(string moduleType) =>
        AllModels.Where(m => m.Types.Contains(moduleType, StringComparer.OrdinalIgnoreCase));

    /// <summary>Models matching a provider + ModuleType combination.</summary>
    public static IEnumerable<CatalogModel> GetByProviderAndModuleType(string providerType, string moduleType) =>
        AllModels.Where(m =>
            string.Equals(m.Provider, providerType, StringComparison.OrdinalIgnoreCase)
            && m.Types.Contains(moduleType, StringComparer.OrdinalIgnoreCase));

    /// <summary>Distinct provider names that have at least one model of the given ModuleType.</summary>
    public static IEnumerable<string> GetProvidersForModuleType(string moduleType) =>
        GetByModuleType(moduleType).Select(m => m.Provider).Distinct(StringComparer.OrdinalIgnoreCase);
}
