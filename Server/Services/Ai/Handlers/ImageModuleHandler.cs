using System.Text.Json;
using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Image generation module. Delegates to the provider so it can request N
/// non-repeated images in a single API call (natively, via the model's
/// `n`/`num_images` parameter) instead of looping with identical prompts.
/// </summary>
public class ImageModuleHandler : IModuleHandler
{
    private readonly IAiProviderRegistry _registry;
    public string ModuleType => "Image";

    public ImageModuleHandler(IAiProviderRegistry registry) => _registry = registry;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var prompt = ctx.GetInputText("input_prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = ctx.GetConfig("imagePrompt", "");
        if (string.IsNullOrWhiteSpace(prompt) && string.IsNullOrWhiteSpace(ctx.GetConfig("systemPrompt", "")))
            return ModuleResult.Failed("Sin prompt de entrada");

        var module = ctx.Node.AiModule;
        var provider = _registry.GetProvider(module.ProviderType);
        if (provider is null)
            return ModuleResult.Failed($"Proveedor '{module.ProviderType}' no disponible");

        var apiKey = module.ApiKey?.EncryptedKey;
        if (string.IsNullOrEmpty(apiKey))
            return ModuleResult.Failed("API Key no configurada");

        // Input files for image-to-image editing
        var inputFiles = new List<byte[]>();
        foreach (var fi in ctx.GetInputFiles("input_prompt"))
        {
            var bytes = await ctx.ReadOutputFileBytesAsync(fi);
            if (bytes is not null)
                inputFiles.Add(bytes);
        }

        var n = ReadImageCount(ctx.Config);
        // If the incoming edge declares a Format, try to disaggregate the JSON
        // payload into per-image prompts. When successful, call the provider N
        // times with n=1 so each image is generated from its own prompt — the
        // batched n=N path produces near-identical variants of the composite
        // prompt and the user sees the same image in every output port.
        var perImagePrompts = n > 1 && !string.IsNullOrWhiteSpace(ctx.GetInputFormat("input_prompt"))
            ? TryExtractPerImagePrompts(prompt, n)
            : null;

        var allImages = new List<byte[]>();
        var contentType = "image/png";
        string? revisedPrompt = null;
        decimal totalCost = 0m;

        if (perImagePrompts is not null && perImagePrompts.Count > 0)
        {
            for (int i = 0; i < perImagePrompts.Count; i++)
            {
                var perPrompt = perImagePrompts[i];
                var perConfig = new Dictionary<string, object>(ctx.Config) { ["n"] = 1, ["numberOfImages"] = 1 };
                var perContext = new AiExecutionContext
                {
                    ModuleType = module.ModuleType,
                    ModelName = module.ModelName,
                    ApiKey = apiKey,
                    Input = perPrompt,
                    ProjectContext = ctx.Project.Context,
                    PreviousExecutionsSummary = ctx.PreviousSummaryContext,
                    MandatoryRules = ctx.MandatoryRules,
                    Configuration = perConfig,
                    InputFiles = inputFiles,
                };

                if (i == 0)
                    StepPayloadBuilder.RecordSentPayload(ctx, perContext, module.ProviderType);

                var perResult = await provider.ExecuteAsync(perContext);
                if (!perResult.Success)
                    return ModuleResult.Failed(perResult.Error ?? $"Error en generacion de imagen {i + 1}");

                if (perResult.FileOutput is { Length: > 0 })
                    allImages.Add(perResult.FileOutput);
                if (perResult.AdditionalFiles is { Count: > 0 })
                    allImages.AddRange(perResult.AdditionalFiles.Where(b => b.Length > 0));

                if (!string.IsNullOrEmpty(perResult.ContentType)) contentType = perResult.ContentType;
                if (revisedPrompt is null)
                    revisedPrompt = perResult.Metadata?.GetValueOrDefault("revisedPrompt")?.ToString();
                totalCost += perResult.EstimatedCost;
            }
        }
        else
        {
            // Single call — the provider reads `n`/`numberOfImages` from ctx.Config and
            // asks the API for all images at once. When N>1 without a per-image
            // contract we prepend a disaggregation rule so the model at least
            // tries to vary each batched output.
            var finalPrompt = n > 1
                ? OutputSchemaHelper.GetMultiImageDisaggregationRule(n) + "\n\n" + prompt
                : prompt;

            var aiContext = new AiExecutionContext
            {
                ModuleType = module.ModuleType,
                ModelName = module.ModelName,
                ApiKey = apiKey,
                Input = finalPrompt,
                ProjectContext = ctx.Project.Context,
                PreviousExecutionsSummary = ctx.PreviousSummaryContext,
                MandatoryRules = ctx.MandatoryRules,
                Configuration = ctx.Config,
                InputFiles = inputFiles,
            };

            StepPayloadBuilder.RecordSentPayload(ctx, aiContext, module.ProviderType);

            var result = await provider.ExecuteAsync(aiContext);
            if (!result.Success)
                return ModuleResult.Failed(result.Error ?? "Error en generacion de imagen");

            if (result.FileOutput is { Length: > 0 })
                allImages.Add(result.FileOutput);
            if (result.AdditionalFiles is { Count: > 0 })
                allImages.AddRange(result.AdditionalFiles.Where(b => b.Length > 0));

            if (!string.IsNullOrEmpty(result.ContentType)) contentType = result.ContentType;
            revisedPrompt = result.Metadata?.GetValueOrDefault("revisedPrompt")?.ToString();
            totalCost = result.EstimatedCost;
        }

        if (allImages.Count == 0)
            return ModuleResult.Failed("El proveedor no devolvio imagenes");

        var ext = contentType switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".jpg"
        };

        var producedFiles = new List<ProducedFile>();
        var outputFiles = new List<OutputFile>();

        for (int i = 0; i < allImages.Count; i++)
        {
            var fileName = allImages.Count > 1 ? $"image_{i + 1}{ext}" : $"output{ext}";
            producedFiles.Add(new ProducedFile
            {
                Data = allImages[i],
                FileName = fileName,
                ContentType = contentType,
            });
            outputFiles.Add(new OutputFile
            {
                FileName = fileName,
                ContentType = contentType,
                FileSize = allImages[i].Length,
                RevisedPrompt = revisedPrompt,
            });
        }

        var output = OutputSchemaHelper.BuildImageOutput(outputFiles, module.ModelName);
        output.Metadata["count"] = allImages.Count;
        output.Metadata["disaggregated"] = perImagePrompts is not null;
        if (perImagePrompts is not null)
            output.Metadata["promptCount"] = perImagePrompts.Count;

        return ModuleResult.Completed(output, totalCost, producedFiles);
    }

    /// <summary>
    /// Parse the upstream JSON payload and extract per-image prompts. Tries a
    /// handful of common key names (images/slides/items/parts/prompts) and a
    /// root-level array as fallback. For each entry, prefers prompt-like keys
    /// (prompt/content/text/description) but falls back to the first non-empty
    /// string value so authors can use ad-hoc keys (e.g. "slide 1"). Returns
    /// null when the payload is not JSON or does not expose a usable list of
    /// length n.
    /// </summary>
    private static List<string>? TryExtractPerImagePrompts(string rawInput, int n)
    {
        if (string.IsNullOrWhiteSpace(rawInput)) return null;

        var trimmed = rawInput.Trim();
        // Strip markdown fences so the model can wrap its response in ```json ... ```
        if (trimmed.StartsWith("```"))
        {
            var nl = trimmed.IndexOf('\n');
            if (nl > 0) trimmed = trimmed[(nl + 1)..];
            if (trimmed.EndsWith("```")) trimmed = trimmed[..^3];
            trimmed = trimmed.Trim();
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(trimmed); }
        catch
        {
            Console.WriteLine("[ImageModule] Per-image disaggregation skipped: payload is not valid JSON.");
            return null;
        }

        using (doc)
        {
            // Case 1: root is itself an array.
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var prompts = ExtractListOfPrompts(doc.RootElement);
                if (prompts.Count == n) return prompts;
                Console.WriteLine($"[ImageModule] Root array had {prompts.Count} prompts, expected {n}; falling back to batched call.");
                return null;
            }

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                Console.WriteLine($"[ImageModule] Per-image disaggregation skipped: root is {doc.RootElement.ValueKind}.");
                return null;
            }

            // Case 2: object with a known "images-like" array key.
            var candidateArrays = new[] { "images", "slides", "items", "parts", "prompts", "generatedImages" };
            foreach (var key in candidateArrays)
            {
                if (!doc.RootElement.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                var prompts = ExtractListOfPrompts(arr);
                if (prompts.Count == n)
                {
                    Console.WriteLine($"[ImageModule] Disaggregating {n} prompts from object key '{key}'.");
                    return prompts;
                }
                if (prompts.Count > 0)
                    Console.WriteLine($"[ImageModule] Key '{key}' had {prompts.Count} prompts, expected {n}.");
            }

            // Case 3: object with a single property whose value is an array of n.
            var props = doc.RootElement.EnumerateObject().ToList();
            if (props.Count == 1 && props[0].Value.ValueKind == JsonValueKind.Array)
            {
                var prompts = ExtractListOfPrompts(props[0].Value);
                if (prompts.Count == n)
                {
                    Console.WriteLine($"[ImageModule] Disaggregating {n} prompts from single-key object '{props[0].Name}'.");
                    return prompts;
                }
            }

            Console.WriteLine($"[ImageModule] Per-image disaggregation skipped: no usable array of {n} prompts found.");
        }

        return null;
    }

    private static List<string> ExtractListOfPrompts(JsonElement arr)
    {
        var prompts = new List<string>();
        foreach (var element in arr.EnumerateArray())
        {
            var text = ExtractPromptFromElement(element);
            if (!string.IsNullOrWhiteSpace(text))
                prompts.Add(text!);
        }
        return prompts;
    }

    private static string? ExtractPromptFromElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString();

        if (element.ValueKind != JsonValueKind.Object) return null;

        // Prefer well-known keys for stability when authors use the canonical shape.
        foreach (var key in new[] { "prompt", "content", "text", "description" })
        {
            if (element.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var s = prop.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }

        // Fallback: ad-hoc keys (e.g. "slide 1") — accept the first non-empty
        // string property so the contract syntax stays flexible.
        foreach (var p in element.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.String)
            {
                var s = p.Value.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    /// <summary>
    /// Mirrors OpenAiProvider.ReadImageCount: reads `n` or `numberOfImages`
    /// from the merged module config, tolerating int/long/double/JsonElement/string.
    /// </summary>
    private static int ReadImageCount(IDictionary<string, object> config)
    {
        int Parse(object? v) => v switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            JsonElement je when je.TryGetInt32(out var ji) => ji,
            string s when int.TryParse(s, out var sp) => sp,
            _ => 1,
        };
        if (config.TryGetValue("n", out var nv)) return Math.Max(1, Parse(nv));
        if (config.TryGetValue("numberOfImages", out var niv)) return Math.Max(1, Parse(niv));
        return 1;
    }
}
