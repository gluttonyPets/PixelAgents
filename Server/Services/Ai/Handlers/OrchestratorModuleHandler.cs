using System.Text.Json;
using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Orchestrator: generates multiple structured outputs from a single prompt.
/// Each output becomes available on a separate output port (output_1, output_2, etc.)
///
/// En modo loop (<see cref="LoopPlanner"/>) cada salida no lleva un valor sino N,
/// uno por parte, y N sale de la duracion de lo que entra por el puerto de
/// duracion dividida entre la del clip del modulo de video que va detras.
/// </summary>
public class OrchestratorModuleHandler : IModuleHandler
{
    private readonly IAiProviderRegistry _registry;
    public string ModuleType => "Orchestrator";

    public OrchestratorModuleHandler(IAiProviderRegistry registry) => _registry = registry;

    /// <summary>Veces que se repite la llamada si el plan del loop no trae las partes pedidas.</summary>
    private const int LoopAttempts = 2;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        if (ctx.GetConfigBool(LoopPlanner.ModeKey))
            return await ExecuteLoopAsync(ctx);

        var prompt = ctx.GetInputText("input_prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return ModuleResult.Failed("Sin instrucciones de entrada");

        var module = ctx.Node.AiModule;
        var pm = ctx.Node.ProjectModule;

        // Use the orchestrator's own model or fallback
        var effectiveModel = ctx.GetConfig("modelName", module.ModelName);
        var effectiveProvider = ctx.GetConfig("providerName", module.ProviderType);

        var provider = _registry.GetProvider(effectiveProvider);
        if (provider is null)
            return ModuleResult.Failed($"Proveedor '{effectiveProvider}' no disponible");

        var apiKey = module.ApiKey?.EncryptedKey
            ?? ctx.GetConfig("apiKey", "");
        if (string.IsNullOrEmpty(apiKey))
            return ModuleResult.Failed("API Key no configurada");

        // Load orchestrator outputs configuration
        var outputs = pm.OrchestratorOutputs?.OrderBy(o => o.SortOrder).ToList() ?? [];
        if (outputs.Count == 0)
            return ModuleResult.Failed("Orquestador sin salidas configuradas");

        // Build system prompt with output schema
        var extraPrompt = ctx.GetConfig("systemPrompt", "");
        var projectContext = string.IsNullOrWhiteSpace(extraPrompt)
            ? ctx.Project.Context
            : $"{ctx.Project.Context}\n\n{extraPrompt}";
        var systemPrompt = OrchestratorSchemaHelper.BuildFixedOutputPrompt(outputs, projectContext);

        var config = new Dictionary<string, object>(ctx.Config)
        {
            ["systemPrompt"] = systemPrompt,
        };

        var aiContext = new AiExecutionContext
        {
            ModuleType = "Text",
            ModelName = effectiveModel,
            ApiKey = apiKey,
            Input = prompt,
            ProjectContext = ctx.Project.Context,
            PreviousExecutionsSummary = ctx.PreviousSummaryContext,
            MandatoryRules = ctx.MandatoryRules,
            SuppressedRuleKeys = ctx.SuppressedRuleKeys,
            VariablesBlock = ctx.VariablesBlock,
            PastExecutionsLearning = ctx.PastExecutionsLearning,
            Configuration = config,
            CancellationToken = ctx.CancellationToken,
        };

        StepPayloadBuilder.RecordSentPayload(ctx, aiContext, effectiveProvider);

        var result = await provider.ExecuteAsync(aiContext);
        if (!result.Success)
            return ModuleResult.Failed(result.Error ?? "Error en orquestador");

        // Parse the structured plan
        var plan = OrchestratorSchemaHelper.ParseFixedPlan(result.TextOutput ?? "");
        if (plan is null)
            return ModuleResult.Failed("No se pudo parsear la salida del orquestador");

        var validationErrors = OrchestratorSchemaHelper.ValidateFixedPlan(plan, outputs);
        if (validationErrors.Count > 0)
            return ModuleResult.Failed("Salida del orquestador invalida: " + string.Join("; ", validationErrors));

        // Build output items
        var items = new List<OutputItem>();
        var contentByKey = plan.Outputs.ToDictionary(o => o.OutputKey, o => o.Content);
        foreach (var output in outputs)
        {
            var content = contentByKey.GetValueOrDefault(output.OutputKey, "");
            items.Add(new OutputItem { Content = content, Label = output.Label });
        }

        var stepOutput = new StepOutput
        {
            Type = "orchestrator",
            Title = plan.Summary,
            Content = $"Orquestador: {outputs.Count} salidas generadas",
            Summary = plan.Summary,
            Items = items,
            Metadata = new Dictionary<string, object>
            {
                ["outputs"] = outputs.Select(o => new Dictionary<string, object>
                {
                    ["outputKey"] = o.OutputKey,
                    ["label"] = o.Label,
                    ["dataType"] = o.DataType,
                }).ToList()
            }
        };

        var producedFiles = new List<ProducedFile>();
        if (!string.IsNullOrEmpty(result.TextOutput))
        {
            producedFiles.Add(new ProducedFile
            {
                Data = System.Text.Encoding.UTF8.GetBytes(result.TextOutput),
                FileName = "orchestrator_plan.json",
                ContentType = "application/json",
            });
        }

        return ModuleResult.Completed(stepOutput, result.EstimatedCost, producedFiles);
    }

    // ═══════════ Modo loop ═══════════

    private async Task<ModuleResult> ExecuteLoopAsync(ModuleExecutionContext ctx)
    {
        var module = ctx.Node.AiModule;
        var pm = ctx.Node.ProjectModule;

        var outputs = pm.OrchestratorOutputs?.OrderBy(o => o.SortOrder).ToList() ?? [];
        if (outputs.Count == 0)
            return ModuleResult.Failed("Orquestador sin salidas configuradas: anade al menos una (sus campos son los de cada parte)");

        // 0. El rectangulo de loop marca que modulos se repiten, y dentro tiene que
        // haber un modulo de duracion: es el que dice cuanto cubre cada vuelta.
        if (ctx.Node.OwnedLoopFrame is not { } frame)
            return ModuleResult.Failed(
                "El orquestador loop no tiene rectangulo de loop. Crealo desde su panel y deja dentro los modulos que se repiten.");
        if (frame.Members.Count == 0)
            return ModuleResult.Failed(
                "El rectangulo de loop esta vacio: agrandalo o mueve dentro los modulos que se repiten.");

        var timed = frame.Members.Where(m => LoopPlanner.IsDurationModule(m.ModuleType)).ToList();
        if (timed.Count == 0)
            return ModuleResult.Failed(
                "Dentro del rectangulo de loop no hay ningun modulo de duracion (video o audio): sin el no se sabe "
                + "cuanto cubre cada vuelta. Mete dentro el modulo de video (p. ej. Leonardo, 5 s por clip).");

        // 1. Duracion de la locucion: el audio o video que llega por la entrada
        // Locucion. Varios ficheros se suman (las tomas de una voz en off).
        var media = (ctx.InputsByPort.GetValueOrDefault(LoopPlanner.NarrationPort) ?? [])
            .Where(d => d.Files is not null)
            .SelectMany(d => d.Files!)
            .Where(f => LoopPlanner.IsTimedMedia(f.ContentType))
            .GroupBy(f => (f.FileId, f.FileName))
            .Select(g => g.First())
            .ToList();
        if (media.Count == 0)
            return ModuleResult.Failed(
                "La entrada Locucion no trae ningun audio ni video: conecta ahi la voz en off (el modulo de voz). "
                + "Su duracion decide cuantas vueltas da el loop; una imagen o un texto no tienen duracion.");

        var totalSeconds = 0d;
        foreach (var file in media)
        {
            var seconds = await VideoAssembler.ProbeDurationAsync(ctx.ResolveOutputFilePath(file), ctx.CancellationToken);
            if (seconds is null)
                return ModuleResult.Failed($"No se pudo medir la duracion de '{file.FileName}' (hace falta ffprobe en el servidor)");
            totalSeconds += seconds.Value;
        }

        // 2. El ritmo: los segundos del modulo de duracion de dentro, o los fijados a mano.
        var pacer = ResolvePacer(ctx, frame);
        if (pacer is null)
            return ModuleResult.Failed(
                $"Los modulos de duracion del rectangulo ({string.Join(", ", timed.Select(t => t.ProjectModule.StepName ?? t.AiModule.Name))}) "
                + "no tienen una duracion de clip conocida. Fija los segundos por vuelta en el panel del orquestador.");

        var needed = LoopPlanner.CountPartsUncapped(totalSeconds, pacer.ClipSeconds);
        var parts = LoopPlanner.CountParts(totalSeconds, pacer.ClipSeconds);
        if (needed > LoopPlanner.MaxParts)
            await ctx.LogWarningAsync(
                $"La entrada pediria {needed} partes y el tope es {LoopPlanner.MaxParts}: "
                + "el final quedara cubierto congelando el ultimo fotograma en el montaje");

        await ctx.LogInfoAsync(
            $"Modo loop: la entrada dura {totalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s "
            + $"y cada parte {pacer.ClipSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s"
            + (pacer.NodeName is null ? " (fijado en el nodo)" : $" (lo marca '{pacer.NodeName}')")
            + $": {parts} vuelta(s)");

        // 3. Tramos, con el texto de la locucion si llega.
        var narration = ctx.GetInputText(LoopPlanner.NarrationPort);
        var windows = LoopPlanner.BuildWindows(parts, pacer.ClipSeconds, totalSeconds, narration);
        if (!string.IsNullOrWhiteSpace(narration))
            await ctx.LogInfoAsync(LoopPlanner.ParseTimestamps(narration).Count > 0
                ? "La locucion trae marcas de tiempo: cada parte recibe el texto exacto de su tramo"
                : "La locucion no trae marcas de tiempo: se reparte por proporcion entre los tramos");

        var prompt = ctx.GetInputText("input_prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = string.IsNullOrWhiteSpace(narration)
                ? "Planifica las partes a partir de su duracion."
                : narration;

        // 4. Llamada a la IA, con un reintento si no devuelve las partes pedidas.
        var effectiveModel = ctx.GetConfig("modelName", module.ModelName);
        var effectiveProvider = ctx.GetConfig("providerName", module.ProviderType);

        var provider = _registry.GetProvider(effectiveProvider);
        if (provider is null)
            return ModuleResult.Failed($"Proveedor '{effectiveProvider}' no disponible");

        var apiKey = module.ApiKey?.EncryptedKey ?? ctx.GetConfig("apiKey", "");
        if (string.IsNullOrEmpty(apiKey))
            return ModuleResult.Failed("API Key no configurada");

        var extraPrompt = ctx.GetConfig("systemPrompt", "");
        var projectContext = string.IsNullOrWhiteSpace(extraPrompt)
            ? ctx.Project.Context
            : $"{ctx.Project.Context}\n\n{extraPrompt}";
        var systemPrompt = LoopPlanner.BuildSystemPrompt(outputs, windows, projectContext);

        LoopPlanner.Plan? plan = null;
        string? rawText = null;
        var totalCost = 0m;
        var errors = new List<string>();

        for (var attempt = 1; attempt <= LoopAttempts; attempt++)
        {
            var input = errors.Count == 0
                ? prompt
                : $"{prompt}\n\nTu respuesta anterior no era valida ({string.Join("; ", errors)}). "
                  + $"Devuelve EXACTAMENTE {parts} partes con todos los campos.";

            var config = new Dictionary<string, object>(ctx.Config)
            {
                ["systemPrompt"] = systemPrompt,
            };

            var aiContext = new AiExecutionContext
            {
                ModuleType = "Text",
                ModelName = effectiveModel,
                ApiKey = apiKey,
                Input = input,
                ProjectContext = ctx.Project.Context,
                PreviousExecutionsSummary = ctx.PreviousSummaryContext,
                MandatoryRules = ctx.MandatoryRules,
                SuppressedRuleKeys = ctx.SuppressedRuleKeys,
                VariablesBlock = ctx.VariablesBlock,
                PastExecutionsLearning = ctx.PastExecutionsLearning,
                Configuration = config,
                CancellationToken = ctx.CancellationToken,
            };

            StepPayloadBuilder.RecordSentPayload(ctx, aiContext, effectiveProvider);

            var result = await provider.ExecuteAsync(aiContext);
            if (!result.Success)
                return ModuleResult.Failed(result.Error ?? "Error en orquestador");

            totalCost += result.EstimatedCost;
            rawText = result.TextOutput;

            plan = LoopPlanner.ParsePlan(rawText);
            errors = plan is null
                ? ["la respuesta no es el JSON pedido"]
                : LoopPlanner.ValidatePlan(plan, outputs, parts);

            if (errors.Count == 0) break;

            if (attempt < LoopAttempts)
                await ctx.LogWarningAsync($"Plan del loop invalido ({string.Join("; ", errors)}): se repite la llamada");
        }

        if (plan is null || errors.Count > 0)
            return ModuleResult.Failed("Salida del orquestador loop invalida: " + string.Join("; ", errors));

        // 5. Cada salida lleva sus N valores separados por tomas.
        var items = outputs
            .Select(o => new OutputItem
            {
                Label = o.Label,
                Content = LoopPlanner.JoinParts(plan.Parts.Select(p => p[o.OutputKey]).ToList()),
            })
            .ToList();

        var stepOutput = new StepOutput
        {
            Type = "orchestrator",
            Title = plan.Summary,
            Content = $"Orquestador loop: {parts} parte(s) x {outputs.Count} salida(s)",
            Summary = plan.Summary,
            Items = items,
            Metadata = new Dictionary<string, object>
            {
                ["loop"] = true,
                ["parts"] = parts,
                ["durationSeconds"] = Math.Round(totalSeconds, 2),
                ["clipSeconds"] = pacer.ClipSeconds,
                ["outputs"] = outputs.Select(o => new Dictionary<string, object>
                {
                    ["outputKey"] = o.OutputKey,
                    ["label"] = o.Label,
                    ["dataType"] = o.DataType,
                }).ToList(),
                ["windows"] = windows.Select(w => new Dictionary<string, object>
                {
                    ["index"] = w.Index,
                    ["start"] = Math.Round(w.Start, 2),
                    ["end"] = Math.Round(w.End, 2),
                    ["narration"] = w.Narration ?? "",
                }).ToList(),
            }
        };

        var producedFiles = new List<ProducedFile>();
        if (!string.IsNullOrEmpty(rawText))
        {
            producedFiles.Add(new ProducedFile
            {
                Data = System.Text.Encoding.UTF8.GetBytes(rawText),
                FileName = "orchestrator_plan.json",
                ContentType = "application/json",
            });
        }

        return ModuleResult.Completed(stepOutput, totalCost, producedFiles);
    }

    /// <summary>Segundos por vuelta fijados en el nodo; si no, los del modulo de duracion de dentro.</summary>
    private static LoopPlanner.Pacer? ResolvePacer(ModuleExecutionContext ctx, LoopFrames.FrameInfo frame)
    {
        var manual = ctx.GetConfig(LoopPlanner.ClipSecondsKey, "");
        if (double.TryParse(manual.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            return new LoopPlanner.Pacer(seconds, null);

        return LoopPlanner.FindPacer(frame);
    }
}
