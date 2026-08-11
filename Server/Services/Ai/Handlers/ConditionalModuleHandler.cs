using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Server.Models;
using Server.Services;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Modulo Condicional: evalua una condicion escrita sobre los datos que le
/// llegan y decide por que rama sigue el pipeline.
///
///   - Si la condicion se cumple, propaga la entrada por <c>output_true</c>.
///   - Si no se cumple, propaga por <c>output_false</c>.
///
/// La rama contraria queda bloqueada: el executor marca sus modulos como
/// <c>Skipped</c> y la ejecucion termina como completada, no como fallida.
/// Si solo se conecta <c>output_true</c>, no cumplirse la condicion equivale a
/// detener ahi el pipeline (los modulos siguientes no se ejecutan).
///
/// Modos de evaluacion (config <c>conditionMode</c>):
///   - <c>auto</c> (por defecto): intenta la evaluacion determinista y, si la
///     condicion no encaja con esa gramatica, la delega en la IA.
///   - <c>expression</c>: solo evaluacion determinista; si no se entiende, el
///     modulo falla en vez de adivinar.
///   - <c>ai</c>: siempre pregunta al modelo.
///
/// El modelo usado en modo IA sale de <c>conditionProvider</c>/<c>conditionModel</c>
/// y, si no estan configurados, del modelo por defecto del tenant
/// (<see cref="AnalystDefaults"/>) segun las API Keys disponibles.
/// </summary>
public class ConditionalModuleHandler : IModuleHandler
{
    private const string InputPort = "input";

    private readonly IAiProviderRegistry _registry;
    private readonly ITenantDbContextFactory _tenantFactory;

    public ConditionalModuleHandler(IAiProviderRegistry registry, ITenantDbContextFactory tenantFactory)
    {
        _registry = registry;
        _tenantFactory = tenantFactory;
    }

    public string ModuleType => ConditionalBranching.ModuleType;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var condition = ctx.GetConfig("condition").Trim();
        if (string.IsNullOrWhiteSpace(condition))
            return ModuleResult.Failed("El modulo condicional no tiene ninguna condicion escrita. Escribela en el inspector del nodo.");

        var mode = NormalizeMode(ctx.GetConfig("conditionMode", "auto"));
        var input = CollectInput(ctx);

        bool met;
        string reason;
        var cost = 0m;

        if (mode is "expression" or "auto")
        {
            var evaluation = ConditionEvaluator.Evaluate(condition, input);
            if (evaluation.Parsed)
            {
                met = evaluation.Value;
                reason = evaluation.Explanation;
                await ctx.LogInfoAsync($"[Condicional] Expresion: {reason}");
                return Decide(ctx, condition, input, "expression", met, reason, cost);
            }

            if (mode == "expression")
                return ModuleResult.Failed(
                    $"No se ha podido interpretar la condicion en modo expresion: {evaluation.Explanation} " +
                    "Revisa la sintaxis o cambia el modo a 'IA' para escribirla en lenguaje natural.");

            await ctx.LogInfoAsync(
                $"[Condicional] La condicion no encaja con la sintaxis de expresiones ({evaluation.Explanation}) " +
                "— se evalua con IA.");
        }

        var (aiResult, aiError, aiCost) = await EvaluateWithAiAsync(ctx, condition, input);
        if (aiResult is null)
            return ModuleResult.Failed(aiError ?? "No se ha podido evaluar la condicion con IA.");

        met = aiResult.Value.Met;
        reason = aiResult.Value.Reason;
        cost = aiCost;
        await ctx.LogInfoAsync($"[Condicional] IA: {(met ? "se cumple" : "no se cumple")} — {reason}");

        return Decide(ctx, condition, input, "ai", met, reason, cost);
    }

    // ── Resultado ──

    private static ModuleResult Decide(
        ModuleExecutionContext ctx, string condition, string input, string mode, bool met, string reason, decimal cost)
    {
        var output = new StepOutput
        {
            Type = "conditional",
            // La entrada se propaga tal cual para que la rama viva reciba los
            // mismos datos que si el condicional no estuviera en medio.
            Content = input,
            Summary = met
                ? $"Condicion cumplida: {condition}"
                : $"Condicion no cumplida: {condition}",
            Files = ctx.InputsByPort.Values
                .SelectMany(list => list)
                .Where(d => d.Files is { Count: > 0 })
                .SelectMany(d => d.Files!)
                .ToList(),
            Metadata = new Dictionary<string, object>
            {
                [ConditionalBranching.MetadataKey] = met,
                ["condition"] = condition,
                ["mode"] = mode,
                ["reason"] = reason,
            },
        };

        return new ModuleResult
        {
            Status = ModuleResultStatus.Completed,
            Output = output,
            Cost = cost,
            BlockedOutputPorts = [.. ConditionalBranching.BlockedPortsFor(met)],
        };
    }

    private static string NormalizeMode(string mode) => mode.Trim().ToLowerInvariant() switch
    {
        "ai" or "ia" => "ai",
        "expression" or "expresion" or "expr" => "expression",
        _ => "auto",
    };

    /// <summary>Texto de todas las entradas conectadas, en orden de puerto.</summary>
    private static string CollectInput(ModuleExecutionContext ctx)
    {
        var direct = ctx.GetInputText(InputPort);
        if (!string.IsNullOrWhiteSpace(direct)) return direct;

        // El puerto canonico es "input", pero un nodo puede venir de una version
        // anterior del editor o traer varias entradas: se juntan todas.
        var texts = ctx.InputsByPort.Values
            .SelectMany(list => list)
            .Select(d => d.TextContent)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim())
            .ToList();

        return string.Join("\n\n", texts);
    }

    // ── Evaluacion con IA ──

    private async Task<((bool Met, string Reason)? Result, string? Error, decimal Cost)> EvaluateWithAiAsync(
        ModuleExecutionContext ctx, string condition, string input)
    {
        var providerType = ctx.GetConfig("conditionProvider").Trim();
        var modelName = ctx.GetConfig("conditionModel").Trim();

        await using var db = _tenantFactory.Create(ctx.TenantDbName);

        if (string.IsNullOrWhiteSpace(providerType) || string.IsNullOrWhiteSpace(modelName))
        {
            var providersWithKeys = await db.ApiKeys
                .Where(k => k.EncryptedKey != null && k.EncryptedKey != "")
                .Select(k => k.ProviderType)
                .Distinct()
                .ToListAsync(ctx.CancellationToken);

            var fallback = AnalystDefaults.Resolve(providersWithKeys);
            if (fallback is null)
                return (null, "No hay ninguna API Key configurada (OpenAI, Anthropic o Google) para evaluar la condicion con IA.", 0m);

            providerType = fallback.Value.Provider;
            modelName = fallback.Value.Model;
        }

        var apiKey = await db.ApiKeys
            .Where(k => k.ProviderType == providerType && k.EncryptedKey != null && k.EncryptedKey != "")
            .OrderBy(k => k.CreatedAt)
            .Select(k => k.EncryptedKey)
            .FirstOrDefaultAsync(ctx.CancellationToken);

        var provider = _registry.GetProvider(providerType);
        if (provider is null || string.IsNullOrWhiteSpace(apiKey))
            return (null, $"No hay API Key o proveedor '{providerType}' disponible para evaluar la condicion.", 0m);

        var aiContext = new AiExecutionContext
        {
            ModuleType = "Text",
            ModelName = modelName,
            ApiKey = apiKey!,
            Input = BuildPrompt(condition, input),
            ProjectContext = ctx.Project.Context,
            Configuration = new Dictionary<string, object>
            {
                ["systemPrompt"] = SystemInstruction,
                ["maxTokens"] = 600,
            },
            CancellationToken = ctx.CancellationToken,
        };

        AiResult result;
        try
        {
            result = await provider.ExecuteAsync(aiContext);
        }
        catch (Exception ex)
        {
            return (null, $"Fallo llamando al modelo para evaluar la condicion: {ex.Message}", 0m);
        }

        if (!result.Success || string.IsNullOrWhiteSpace(result.TextOutput))
            return (null, result.Error ?? "El modelo no devolvio respuesta al evaluar la condicion.", result.EstimatedCost);

        if (!TryParseVerdict(result.TextOutput!, out var met, out var reason))
            return (null,
                $"No se ha entendido la respuesta del modelo al evaluar la condicion: {Truncate(result.TextOutput!, 200)}",
                result.EstimatedCost);

        return ((met, reason), null, result.EstimatedCost);
    }

    private const string SystemInstruction =
        "Eres un evaluador de condiciones dentro de un pipeline automatico. Recibes una CONDICION y unos DATOS. " +
        "Decides unicamente si la condicion se cumple sobre esos datos. " +
        "Responde SOLO con un objeto JSON valido, sin texto alrededor ni bloques de codigo, con esta forma exacta: " +
        "{\"cumple\": true|false, \"motivo\": \"explicacion breve en una frase\"}. " +
        "No pidas aclaraciones ni propongas alternativas: si la informacion es ambigua, decide con lo que tienes.";

    private static string BuildPrompt(string condition, string input)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CONDICION A EVALUAR:");
        sb.AppendLine(condition);
        sb.AppendLine();
        sb.AppendLine("DATOS DE ENTRADA:");
        sb.AppendLine(string.IsNullOrWhiteSpace(input) ? "(sin datos de entrada)" : input);
        return sb.ToString();
    }

    /// <summary>
    /// Lee el veredicto del modelo. Acepta el JSON pedido, el mismo JSON
    /// envuelto en un bloque de codigo o en {"content": "..."} (algunos modelos
    /// lo hacen) y, como ultimo recurso, un "si"/"no" suelto.
    /// </summary>
    internal static bool TryParseVerdict(string raw, out bool met, out string reason)
    {
        met = false;
        reason = "";

        var json = ExtractJsonObject(raw);
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (TryReadVerdict(doc.RootElement, out met, out reason))
                    return true;

                // {"content": "{\"cumple\": true}"}
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("content", out var inner)
                    && inner.ValueKind == JsonValueKind.String)
                {
                    var innerJson = ExtractJsonObject(inner.GetString() ?? "");
                    if (innerJson is not null)
                    {
                        using var innerDoc = JsonDocument.Parse(innerJson);
                        if (TryReadVerdict(innerDoc.RootElement, out met, out reason))
                            return true;
                    }
                }
            }
            catch (JsonException)
            {
                // Cae al reconocimiento por texto plano.
            }
        }

        var plain = raw.Trim().Trim('.', '!', '"', '\'').ToLowerInvariant();
        if (plain is "true" or "si" or "sí" or "yes" or "cumple" or "se cumple")
        {
            met = true;
            reason = "El modelo respondio que la condicion se cumple.";
            return true;
        }
        if (plain is "false" or "no" or "no cumple" or "no se cumple")
        {
            met = false;
            reason = "El modelo respondio que la condicion no se cumple.";
            return true;
        }

        return false;
    }

    private static bool TryReadVerdict(JsonElement root, out bool met, out string reason)
    {
        met = false;
        reason = "";
        if (root.ValueKind != JsonValueKind.Object) return false;

        bool? value = null;
        foreach (var name in new[] { "cumple", "result", "value", "met", "condicion" })
        {
            if (!root.TryGetProperty(name, out var prop)) continue;
            value = prop.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => ParseBoolWord(prop.GetString()),
                _ => null,
            };
            if (value is not null) break;
        }

        if (value is null) return false;

        met = value.Value;
        reason = root.TryGetProperty("motivo", out var motivo) && motivo.ValueKind == JsonValueKind.String
            ? motivo.GetString() ?? ""
            : root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? ""
                : "";

        if (string.IsNullOrWhiteSpace(reason))
            reason = met ? "La condicion se cumple." : "La condicion no se cumple.";

        return true;
    }

    private static bool? ParseBoolWord(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "true" or "si" or "sí" or "yes" or "1" => true,
        "false" or "no" or "0" => false,
        _ => null,
    };

    private static string? ExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = cleaned.IndexOf('\n');
            if (firstBreak >= 0) cleaned = cleaned[(firstBreak + 1)..];
            var fence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) cleaned = cleaned[..fence];
            cleaned = cleaned.Trim();
        }

        var start = cleaned.IndexOf('{');
        var end = cleaned.LastIndexOf('}');
        return start >= 0 && end > start ? cleaned[start..(end + 1)] : null;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}
