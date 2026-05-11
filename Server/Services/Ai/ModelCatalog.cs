namespace Server.Services.Ai;

/// <summary>
/// Server-side mirror of the model catalog displayed in the Blazor client
/// (Client/Pages/Modules.razor: AllModels). Used by features that need to
/// offer the same list of models that the user sees when creating a module
/// without depending on which AiModules they happen to have saved.
///
/// Keep in sync with the client catalog. When a new model is added in the
/// Razor page, mirror it here.
/// </summary>
public static class ModelCatalog
{
    public record CatalogModel(string Id, string DisplayName, string Provider, string[] Types);

    public static readonly CatalogModel[] AllModels =
    [
        // ─── OpenAI: Text ───
        new("gpt-5.4",          "GPT-5.4",          "OpenAI", ["Text","Orchestrator","Coordinator"]),
        new("gpt-5.4-pro",      "GPT-5.4 Pro",      "OpenAI", ["Text"]),
        new("gpt-5.2",          "GPT-5.2",          "OpenAI", ["Text"]),
        new("gpt-5.1",          "GPT-5.1",          "OpenAI", ["Text"]),
        new("gpt-5",            "GPT-5",            "OpenAI", ["Text"]),
        new("gpt-5-mini",       "GPT-5 Mini",       "OpenAI", ["Text"]),
        new("gpt-5-nano",       "GPT-5 Nano",       "OpenAI", ["Text"]),
        new("gpt-4.1",          "GPT-4.1",          "OpenAI", ["Text"]),
        new("gpt-4.1-mini",     "GPT-4.1 Mini",     "OpenAI", ["Text"]),
        new("gpt-4.1-nano",     "GPT-4.1 Nano",     "OpenAI", ["Text"]),
        new("gpt-4o",           "GPT-4o",           "OpenAI", ["Text","Orchestrator","Coordinator"]),
        new("gpt-4o-mini",      "GPT-4o Mini",      "OpenAI", ["Text","Orchestrator","Coordinator"]),
        new("o3",               "o3",               "OpenAI", ["Text"]),
        new("o3-mini",          "o3 Mini",          "OpenAI", ["Text"]),
        new("o4-mini",          "o4 Mini",          "OpenAI", ["Text"]),

        // ─── OpenAI: Image ───
        new("gpt-image-1.5",    "GPT Image 1.5",    "OpenAI", ["Image"]),
        new("gpt-image-1",      "GPT Image 1",      "OpenAI", ["Image"]),
        new("gpt-image-1-mini", "GPT Image 1 Mini", "OpenAI", ["Image"]),
        new("dall-e-3",         "DALL-E 3",         "OpenAI", ["Image"]),
        new("dall-e-2",         "DALL-E 2",         "OpenAI", ["Image"]),

        // ─── Anthropic: Text ───
        new("claude-opus-4-6",             "Claude Opus 4.6",   "Anthropic", ["Text"]),
        new("claude-sonnet-4-6",           "Claude Sonnet 4.6", "Anthropic", ["Text","Orchestrator","Coordinator"]),
        new("claude-opus-4-5-20251124",    "Claude Opus 4.5",   "Anthropic", ["Text"]),
        new("claude-sonnet-4-5-20250929",  "Claude Sonnet 4.5", "Anthropic", ["Text"]),
        new("claude-haiku-4-5-20251001",   "Claude Haiku 4.5",  "Anthropic", ["Text","Orchestrator","Coordinator"]),
        new("claude-opus-4-1-20250805",    "Claude Opus 4.1",   "Anthropic", ["Text"]),
        new("claude-sonnet-4-20250514",    "Claude Sonnet 4",   "Anthropic", ["Text"]),
        new("claude-3-5-haiku-20241022",   "Claude 3.5 Haiku",  "Anthropic", ["Text"]),

        // ─── xAI: Text ───
        new("grok-4-0709",                "Grok 4",                    "xAI", ["Text","Orchestrator","Coordinator"]),
        new("grok-4-1-fast-reasoning",    "Grok 4.1 Fast (Reasoning)", "xAI", ["Text","Orchestrator","Coordinator"]),
        new("grok-4-1-fast-non-reasoning","Grok 4.1 Fast",             "xAI", ["Text","Orchestrator","Coordinator"]),
        new("grok-code-fast-1",           "Grok Code Fast",            "xAI", ["Text"]),
        new("grok-3",                     "Grok 3",                    "xAI", ["Text","Orchestrator","Coordinator"]),
        new("grok-3-fast",                "Grok 3 Fast",               "xAI", ["Text","Orchestrator","Coordinator"]),
        new("grok-3-mini",                "Grok 3 Mini",               "xAI", ["Text","Orchestrator","Coordinator"]),
        new("grok-3-mini-fast",           "Grok 3 Mini Fast",          "xAI", ["Text","Orchestrator","Coordinator"]),
        new("grok-2",                     "Grok 2",                    "xAI", ["Text"]),
        new("grok-2-vision",              "Grok 2 Vision",             "xAI", ["Text"]),

        // ─── xAI: Image ───
        new("grok-imagine-image",     "Grok Imagine",     "xAI", ["Image"]),
        new("grok-imagine-image-pro", "Grok Imagine Pro", "xAI", ["Image"]),

        // ─── Google: Text ───
        new("gemini-2.5-flash",     "Gemini 2.5 Flash",      "Google", ["Text","Orchestrator","Coordinator"]),
        new("gemini-2.5-pro",       "Gemini 2.5 Pro",        "Google", ["Text"]),
        new("gemini-2.0-flash-lite","Gemini 2.0 Flash Lite", "Google", ["Text"]),
        new("gemini-1.5-pro",       "Gemini 1.5 Pro",        "Google", ["Text"]),
        new("gemini-1.5-flash",     "Gemini 1.5 Flash",      "Google", ["Text"]),

        // ─── Google: Image ───
        new("gemini-2.5-flash-image",         "Gemini 2.5 Flash Image",         "Google", ["Image"]),
        new("gemini-3.1-flash-image-preview", "Gemini 3.1 Flash Image Preview", "Google", ["Image"]),
        new("gemini-3-pro-image-preview",     "Gemini 3 Pro Image Preview",     "Google", ["Image"]),

        // ─── Leonardo AI: Image ───
        new("leonardo-phoenix",       "Leonardo Phoenix 1.0",  "LeonardoAI", ["Image"]),
        new("leonardo-phoenix-0.9",   "Leonardo Phoenix 0.9",  "LeonardoAI", ["Image"]),
        new("leonardo-flux-dev",      "Leonardo Flux Dev",     "LeonardoAI", ["Image"]),
        new("leonardo-flux-schnell",  "Leonardo Flux Schnell", "LeonardoAI", ["Image"]),
    ];

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
