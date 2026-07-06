using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;
using Server.Services.Ai;

namespace Server.Services.Telegram
{
    /// <summary>
    /// Shared logic for processing Telegram updates (used by both webhook and polling).
    /// </summary>
    public class TelegramUpdateHandler
    {
        private readonly CoreDbContext _coreDb;
        private readonly ITenantDbContextFactory _factory;
        private readonly IPipelineExecutor _executor;
        private readonly TelegramService _telegram;
        private readonly IPromptPlannerService _planner;

        // Defaults used when the user asks for a new planning from Telegram.
        private const string PlanningModel = "gpt-4o-mini";
        private const int PlanningCount = 5;

        // Buttons offered to the user every time we are waiting for input on an Interaction module.
        private static readonly List<(string Label, string CallbackData)> ControlOptions =
        [
            ("Continuar", "continue"),
            ("Abortar", "abort"),
            ("Reiniciar", "restart"),
            ("Editar", "edit"),
        ];

        public TelegramUpdateHandler(
            CoreDbContext coreDb,
            ITenantDbContextFactory factory,
            IPipelineExecutor executor,
            TelegramService telegram,
            IPromptPlannerService planner)
        {
            _coreDb = coreDb;
            _factory = factory;
            _executor = executor;
            _telegram = telegram;
            _planner = planner;
        }

        private class EditFlowState
        {
            public string OutputKind { get; set; } = "text"; // "image" or "text"
            public string? ProviderType { get; set; }
            public string? SelectedModelName { get; set; }
        }

        public async Task ProcessUpdateAsync(JsonElement json)
        {
            var (text, chatId, callbackQueryId, messageDate) = TelegramService.ParseIncomingUpdate(json);

            Console.WriteLine($"[TG-Update] Parsed — text={text}, chatId={chatId}, callbackQueryId={callbackQueryId}, messageDate={messageDate:O}");

            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(chatId))
            {
                Console.WriteLine("[TG-Update] Ignored: text or chatId is empty");
                return;
            }

            var normalizedChatId = chatId.Trim();

            // Find a valid correlation, skipping stale ones whose executions are no longer waiting
            var correlation = await FindValidCorrelationAsync(normalizedChatId, messageDate, callbackQueryId);

            if (correlation is null)
            {
                var pending = await _coreDb.TelegramCorrelations
                    .Where(c => !c.IsResolved)
                    .Select(c => new { c.ChatId, c.ExecutionId, c.CreatedAt })
                    .ToListAsync();
                Console.WriteLine($"[TG-Update] No correlation found for chatId={normalizedChatId}. Pending: {JsonSerializer.Serialize(pending)}");
                return;
            }

            Console.WriteLine($"[TG-Update] Matched correlation {correlation.Id} for execution {correlation.ExecutionId}");

            await using var db = _factory.Create(correlation.TenantDbName);

            async Task<TelegramConfig?> GetTgConfigAsync()
            {
                var exec = await db.ProjectExecutions.FindAsync(correlation.ExecutionId);
                if (exec is null) return null;
                var proj = await db.Projects
                    .Include(p => p.TelegramConnection)
                    .FirstOrDefaultAsync(p => p.Id == exec.ProjectId);
                if (proj?.TelegramConnection is null) return null;
                return new TelegramConfig
                {
                    BotToken = proj.TelegramConnection.BotToken,
                    ChatId = proj.TelegramConnection.ChatId,
                };
            }

            try
            {
                // Answer callback query (removes loading spinner from button)
                if (!string.IsNullOrWhiteSpace(callbackQueryId))
                {
                    var tgConfig = await GetTgConfigAsync();
                    if (tgConfig is not null)
                    {
                        try { await _telegram.AnswerCallbackQueryAsync(tgConfig.BotToken, callbackQueryId); }
                        catch { /* non-critical */ }
                    }
                }

                // State: awaiting_planning — a scheduled run had no prompt and asked the user
                // to describe a new planning from the chat. The reply text becomes the planner
                // instructions; the generated prompts are queued for upcoming scheduled runs.
                if (correlation.State == "awaiting_planning")
                {
                    await HandlePlanningReplyAsync(correlation, db, text.Trim());
                    return;
                }

                // State: awaiting_restart
                if (correlation.State == "awaiting_restart")
                {
                    var clarification = text.Trim().ToLowerInvariant() == "ok" ? null : text.Trim();

                    var execForRestart = await db.ProjectExecutions.FindAsync(correlation.ExecutionId);
                    var originalInput = execForRestart?.UserInput ?? "";
                    Guid? projectIdForRestart = execForRestart?.ProjectId;

                    // Fallback: if UserInput on the execution is empty, try to recover it
                    // from the paused state JSON (PausedGraphState serializes as camelCase).
                    if (string.IsNullOrWhiteSpace(originalInput) && execForRestart?.PausedStepData is not null)
                    {
                        try
                        {
                            var pauseDoc = JsonDocument.Parse(execForRestart.PausedStepData);
                            if (pauseDoc.RootElement.TryGetProperty("userInput", out var uiProp)
                                || pauseDoc.RootElement.TryGetProperty("UserInput", out uiProp))
                                originalInput = uiProp.GetString() ?? "";
                        }
                        catch { }
                    }

                    await _executor.AbortFromInteractionAsync(correlation.ExecutionId, db, correlation.TenantDbName);
                    await _executor.CancelQueuedInteractionsAsync(correlation.ExecutionId);

                    if (projectIdForRestart is not null)
                    {
                        var restartInput = string.IsNullOrWhiteSpace(clarification)
                            ? originalInput
                            : $"{originalInput}\n\nAclaracion del usuario: {clarification}";

                        await _executor.ExecuteAsync(projectIdForRestart.Value, restartInput, db, correlation.TenantDbName);

                        var tgConfig = await GetTgConfigAsync();
                        if (tgConfig is not null)
                        {
                            var restartMsg = string.IsNullOrWhiteSpace(clarification)
                                ? "🔄 Pipeline reiniciado."
                                : $"🔄 Pipeline reiniciado con aclaracion: \"{clarification}\"";
                            try { await _telegram.SendTextMessageAsync(tgConfig, restartMsg); }
                            catch { /* non-critical */ }
                        }
                    }

                    correlation.IsResolved = true;
                    await _coreDb.SaveChangesAsync();
                    return;
                }

                // State: edit_select_provider — user is choosing which provider to use for the edit
                if (correlation.State == "edit_select_provider")
                {
                    var editState = ParseEditState(correlation.EditStateData) ?? new EditFlowState();
                    if (text.StartsWith("edit_provider:", StringComparison.OrdinalIgnoreCase))
                    {
                        var providerType = text["edit_provider:".Length..].Trim();
                        if (string.IsNullOrWhiteSpace(providerType))
                        {
                            await SendEditProviderChoicesAsync(correlation, db, await GetTgConfigAsync(), editState.OutputKind);
                            return;
                        }

                        editState.ProviderType = providerType;
                        correlation.State = "edit_select_model";
                        correlation.EditStateData = JsonSerializer.Serialize(editState);
                        await _coreDb.SaveChangesAsync();
                        await SendEditModelChoicesAsync(correlation, db, await GetTgConfigAsync(), editState.OutputKind, providerType);
                        return;
                    }

                    // User sent something else — re-prompt with provider buttons
                    await SendEditProviderChoicesAsync(correlation, db, await GetTgConfigAsync(), editState.OutputKind);
                    return;
                }

                // State: edit_select_model — user is choosing which model from the catalog to use
                if (correlation.State == "edit_select_model")
                {
                    var editState = ParseEditState(correlation.EditStateData) ?? new EditFlowState();
                    if (text.StartsWith("edit_model:", StringComparison.OrdinalIgnoreCase))
                    {
                        var modelId = text["edit_model:".Length..].Trim();
                        var moduleType = string.Equals(editState.OutputKind, "image", StringComparison.OrdinalIgnoreCase) ? "Image" : "Text";
                        var providerType = editState.ProviderType ?? "";
                        var catalogModel = ModelCatalog.GetByProviderAndModuleType(providerType, moduleType)
                            .FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));

                        if (catalogModel is null)
                        {
                            await SendEditModelChoicesAsync(correlation, db, await GetTgConfigAsync(), editState.OutputKind, editState.ProviderType);
                            return;
                        }

                        editState.SelectedModelName = catalogModel.Id;
                        correlation.State = "edit_awaiting_prompt";
                        correlation.EditStateData = JsonSerializer.Serialize(editState);
                        await _coreDb.SaveChangesAsync();

                        var tgConfig = await GetTgConfigAsync();
                        if (tgConfig is not null)
                        {
                            try
                            {
                                await _telegram.SendTextMessageAsync(tgConfig,
                                    $"✏️ Modelo seleccionado: {catalogModel.DisplayName}\n¿Que quieres editar? Envia tu instruccion.");
                            }
                            catch { /* non-critical */ }
                        }
                        return;
                    }

                    // User sent something else — re-prompt with model buttons for the previously chosen provider
                    await SendEditModelChoicesAsync(correlation, db, await GetTgConfigAsync(), editState.OutputKind, editState.ProviderType);
                    return;
                }

                // State: edit_awaiting_prompt — user is sending the edit instruction
                if (correlation.State == "edit_awaiting_prompt")
                {
                    var editState = ParseEditState(correlation.EditStateData);
                    var tgConfig = await GetTgConfigAsync();
                    if (editState is null
                        || string.IsNullOrWhiteSpace(editState.ProviderType)
                        || string.IsNullOrWhiteSpace(editState.SelectedModelName))
                    {
                        if (tgConfig is not null)
                            try { await _telegram.SendTextMessageAsync(tgConfig, "⚠️ Estado de edicion perdido. Cancelando."); } catch { }
                        await ResetToWaitingAsync(correlation, tgConfig, sendButtons: true);
                        return;
                    }

                    var editPrompt = text.Trim();
                    EditOutputResult result;
                    try
                    {
                        result = await _executor.EditPausedOutputAsync(
                            correlation.ExecutionId,
                            editState.ProviderType,
                            editState.SelectedModelName,
                            editPrompt,
                            db,
                            correlation.TenantDbName);
                    }
                    catch (Exception ex)
                    {
                        result = new EditOutputResult { Success = false, Error = ex.Message };
                    }

                    if (tgConfig is not null)
                    {
                        if (!result.Success)
                        {
                            try { await _telegram.SendTextMessageAsync(tgConfig, $"⚠️ Error en edicion: {result.Error}"); }
                            catch { /* non-critical */ }
                        }
                        else
                        {
                            try
                            {
                                if (!string.IsNullOrWhiteSpace(result.TextResponse))
                                    await _telegram.SendTextMessageAsync(tgConfig, result.TextResponse);
                                foreach (var f in result.Files)
                                {
                                    if (f.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                                        await _telegram.SendVideoAsync(tgConfig, f.Data, f.FileName);
                                    else
                                        await _telegram.SendPhotoAsync(tgConfig, f.Data, f.FileName);
                                }
                            }
                            catch { /* non-critical */ }
                        }
                    }

                    await ResetToWaitingAsync(correlation, tgConfig, sendButtons: true);
                    return;
                }

                // State: waiting — process pipeline control buttons
                if (text == "abort" || text.Contains("Abortar"))
                {
                    await _executor.AbortFromInteractionAsync(correlation.ExecutionId, db, correlation.TenantDbName);
                    await _executor.CancelQueuedInteractionsAsync(correlation.ExecutionId);
                    correlation.IsResolved = true;
                    await _coreDb.SaveChangesAsync();

                    var tgConfig = await GetTgConfigAsync();
                    if (tgConfig is not null)
                    {
                        try { await _telegram.SendTextMessageAsync(tgConfig, "❌ Pipeline abortado. Interacciones pendientes canceladas."); }
                        catch { /* non-critical */ }
                    }
                    return;
                }

                if (text == "restart" || text.Contains("Reiniciar"))
                {
                    correlation.State = "awaiting_restart";
                    await _coreDb.SaveChangesAsync();

                    var tgConfig = await GetTgConfigAsync();
                    if (tgConfig is not null)
                    {
                        try
                        {
                            await _telegram.SendTextMessageAsync(tgConfig,
                                "🔄 Escribe una aclaracion para reiniciar el pipeline, o envia \"ok\" para reiniciar sin cambios.");
                        }
                        catch { /* non-critical */ }
                    }
                    return;
                }

                if (text == "edit" || text.Contains("Editar"))
                {
                    var tgConfig = await GetTgConfigAsync();
                    var info = await _executor.GetPausedOutputInfoAsync(correlation.ExecutionId, db);
                    if (info is null)
                    {
                        if (tgConfig is not null)
                            try { await _telegram.SendTextMessageAsync(tgConfig, "⚠️ No hay output pausado para editar."); } catch { }
                        return;
                    }

                    var editState = new EditFlowState { OutputKind = info.OutputKind };
                    correlation.State = "edit_select_provider";
                    correlation.EditStateData = JsonSerializer.Serialize(editState);
                    await _coreDb.SaveChangesAsync();
                    await SendEditProviderChoicesAsync(correlation, db, tgConfig, info.OutputKind);
                    return;
                }

                if (text == "continue" || text.Contains("Continuar"))
                {
                    // "Continuar" with no extra text — keep the original behaviour: forward "continue" as the response.
                }

                Console.WriteLine($"[TG-Update] Resuming execution {correlation.ExecutionId}, moduleId={correlation.ProjectModuleId}");
                await _executor.ResumeFromInteractionAsync(correlation.ExecutionId, text, db, correlation.TenantDbName);
                correlation.IsResolved = true;
                await _coreDb.SaveChangesAsync();

                // Send next queued interaction message if any
                await _executor.SendNextQueuedInteractionAsync(correlation.ExecutionId, normalizedChatId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TG-Update] ERROR processing update for correlation {correlation.Id}: {ex}");

                // Resolve the correlation to prevent infinite error loops —
                // a failed resume should not block subsequent interactions
                try
                {
                    correlation.IsResolved = true;
                    await _coreDb.SaveChangesAsync();
                }
                catch { /* non-critical */ }

                // Try to notify the user about the error
                try
                {
                    var tgConfig = await GetTgConfigAsync();
                    if (tgConfig is not null)
                        await _telegram.SendTextMessageAsync(tgConfig, $"⚠️ Error al procesar respuesta: {ex.Message}");
                }
                catch { /* non-critical */ }
            }
        }

        /// <summary>
        /// Handles the user's reply to an "awaiting_planning" request: turns the message into a
        /// new planning (via the prompt planner, falling back to one prompt per line) and queues
        /// the resulting prompts so the next scheduled runs have topics to work on.
        /// </summary>
        private async Task HandlePlanningReplyAsync(
            TelegramCorrelation correlation, UserDbContext db, string instructions)
        {
            if (correlation.ProjectId is not { } projectId)
            {
                correlation.IsResolved = true;
                await _coreDb.SaveChangesAsync();
                return;
            }

            var project = await db.Projects
                .Include(p => p.TelegramConnection)
                .FirstOrDefaultAsync(p => p.Id == projectId);

            TelegramConfig? tgConfig = project?.TelegramConnection is null
                ? null
                : new TelegramConfig
                {
                    BotToken = project.TelegramConnection.BotToken,
                    ChatId = project.TelegramConnection.ChatId,
                };

            if (project is null)
            {
                correlation.IsResolved = true;
                await _coreDb.SaveChangesAsync();
                return;
            }

            // Prefer the AI planner; if it can't run (e.g. no OpenAI key) treat each non-empty
            // line the user sent as a literal prompt so the feature still works without a key.
            List<string> prompts;
            var result = await _planner.GenerateAsync(db, projectId, PlanningModel, PlanningCount, instructions);
            if (result.Success && result.Prompts.Count > 0)
            {
                prompts = result.Prompts;
            }
            else
            {
                prompts = instructions
                    .Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();
            }

            if (prompts.Count == 0)
            {
                if (tgConfig is not null)
                    try { await _telegram.SendTextMessageAsync(tgConfig, "⚠️ No pude generar ninguna temática. Vuelve a intentarlo describiendo los temas."); }
                    catch { /* non-critical */ }
                // Keep the correlation open so the user can retry.
                return;
            }

            var now = DateTime.UtcNow;
            var maxOrder = await db.PlannedPrompts
                .Where(p => p.ProjectId == projectId)
                .Select(p => (int?)p.OrderIndex)
                .MaxAsync() ?? -1;

            var idx = maxOrder + 1;
            foreach (var content in prompts)
            {
                db.PlannedPrompts.Add(new PlannedPrompt
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    OrderIndex = idx++,
                    Content = content,
                    Status = PlannedPromptStatus.Pending,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            await db.SaveChangesAsync();

            correlation.IsResolved = true;
            await _coreDb.SaveChangesAsync();

            if (tgConfig is not null)
            {
                var confirm = $"✅ Nueva planificación creada con {prompts.Count} temática(s). " +
                    "Se usarán en las próximas ejecuciones programadas.";
                try { await _telegram.SendTextMessageAsync(tgConfig, confirm); }
                catch { /* non-critical */ }
            }
        }

        /// <summary>
        /// Find a valid correlation for this chatId, skipping and resolving stale ones
        /// whose executions are no longer in WaitingForInput status.
        /// </summary>
        private async Task<TelegramCorrelation?> FindValidCorrelationAsync(
            string chatId, DateTimeOffset? messageDate, string? callbackQueryId)
        {
            var candidates = await _coreDb.TelegramCorrelations
                .Where(c => !c.IsResolved && c.ChatId == chatId && c.State != "queued")
                .OrderBy(c => c.CreatedAt) // FIFO: oldest unresolved first so user responds in send order
                .Take(10) // safety limit
                .ToListAsync();

            Console.WriteLine($"[TG-Update] FindValidCorrelation: {candidates.Count} candidate(s) for chatId={chatId}");
            foreach (var c in candidates)
                Console.WriteLine($"  Candidate {c.Id}: execId={c.ExecutionId}, module={c.ProjectModuleId}, state={c.State}, created={c.CreatedAt:O}");

            foreach (var candidate in candidates)
            {
                // Reject text messages sent before the correlation was created (stale messages)
                // Callback queries (button presses) are exempt — always intentional
                if (messageDate.HasValue && string.IsNullOrWhiteSpace(callbackQueryId))
                {
                    var tolerance = TimeSpan.FromSeconds(5);
                    if (messageDate.Value < candidate.CreatedAt - tolerance)
                    {
                        Console.WriteLine($"[TG-Update] Skipping correlation {candidate.Id}: message is older than correlation");
                        continue;
                    }
                }

                // For these out-of-band states, the execution may not be in WaitingForInput — that's OK
                if (candidate.State is "awaiting_restart" or "edit_select_provider" or "edit_select_model" or "edit_awaiting_prompt" or "awaiting_planning")
                    return candidate;

                // Verify the execution is actually still waiting for input
                await using var db = _factory.Create(candidate.TenantDbName);
                var execution = await db.ProjectExecutions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == candidate.ExecutionId);

                if (execution is null)
                {
                    Console.WriteLine($"[TG-Update] Resolving stale correlation {candidate.Id}: execution not found");
                    candidate.IsResolved = true;
                    await _coreDb.SaveChangesAsync();
                    continue;
                }

                if (execution.Status != "WaitingForInput")
                {
                    Console.WriteLine($"[TG-Update] Resolving stale correlation {candidate.Id}: execution status is '{execution.Status}', not WaitingForInput");
                    candidate.IsResolved = true;
                    await _coreDb.SaveChangesAsync();
                    continue;
                }

                return candidate;
            }

            return null;
        }

        private static EditFlowState? ParseEditState(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<EditFlowState>(json); }
            catch { return null; }
        }

        private async Task SendEditProviderChoicesAsync(
            TelegramCorrelation correlation,
            UserDbContext db,
            TelegramConfig? tgConfig,
            string outputKind)
        {
            if (tgConfig is null) return;

            var moduleType = string.Equals(outputKind, "image", StringComparison.OrdinalIgnoreCase) ? "Image" : "Text";

            // Providers with at least one model of this type in the catalog
            var catalogProviders = ModelCatalog.GetProvidersForModuleType(moduleType)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Providers for which the tenant has an API Key configured
            var tenantProviders = await db.ApiKeys
                .Select(k => k.ProviderType)
                .Distinct()
                .ToListAsync();

            var providers = tenantProviders
                .Where(p => catalogProviders.Contains(p))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (providers.Count == 0)
            {
                try
                {
                    await _telegram.SendTextMessageAsync(tgConfig,
                        $"⚠️ No tienes API Keys configuradas para proveedores con modelos de tipo {moduleType}.");
                }
                catch { /* non-critical */ }
                await ResetToWaitingAsync(correlation, tgConfig, sendButtons: true);
                return;
            }

            var buttons = providers
                .Select(p => (p, $"edit_provider:{p}"))
                .ToList();

            try
            {
                await _telegram.SendTextMessageWithOptionsAsync(tgConfig,
                    $"🔌 Elige el proveedor para editar ({moduleType.ToLowerInvariant()}):",
                    buttons);
            }
            catch { /* non-critical */ }
        }

        private async Task SendEditModelChoicesAsync(
            TelegramCorrelation correlation,
            UserDbContext db,
            TelegramConfig? tgConfig,
            string outputKind,
            string? providerType)
        {
            if (tgConfig is null) return;

            if (string.IsNullOrWhiteSpace(providerType))
            {
                // Provider was lost — go back to provider selection
                correlation.State = "edit_select_provider";
                await _coreDb.SaveChangesAsync();
                await SendEditProviderChoicesAsync(correlation, db, tgConfig, outputKind);
                return;
            }

            var moduleType = string.Equals(outputKind, "image", StringComparison.OrdinalIgnoreCase) ? "Image" : "Text";

            // Verify the tenant still has an API Key for this provider
            var hasApiKey = await db.ApiKeys.AnyAsync(k => k.ProviderType == providerType);
            if (!hasApiKey)
            {
                try
                {
                    await _telegram.SendTextMessageAsync(tgConfig,
                        $"⚠️ Ya no tienes API Key para {providerType}.");
                }
                catch { /* non-critical */ }
                correlation.State = "edit_select_provider";
                await _coreDb.SaveChangesAsync();
                await SendEditProviderChoicesAsync(correlation, db, tgConfig, outputKind);
                return;
            }

            var models = ModelCatalog.GetByProviderAndModuleType(providerType, moduleType).ToList();
            if (models.Count == 0)
            {
                try
                {
                    await _telegram.SendTextMessageAsync(tgConfig,
                        $"⚠️ {providerType} no tiene modelos de tipo {moduleType} en el catalogo.");
                }
                catch { /* non-critical */ }
                correlation.State = "edit_select_provider";
                await _coreDb.SaveChangesAsync();
                await SendEditProviderChoicesAsync(correlation, db, tgConfig, outputKind);
                return;
            }

            var buttons = models
                .Select(m => (m.DisplayName, $"edit_model:{m.Id}"))
                .ToList();

            try
            {
                await _telegram.SendTextMessageWithOptionsAsync(tgConfig,
                    $"🎨 Elige un modelo de {providerType} para editar:",
                    buttons);
            }
            catch { /* non-critical */ }
        }

        private async Task ResetToWaitingAsync(
            TelegramCorrelation correlation,
            TelegramConfig? tgConfig,
            bool sendButtons)
        {
            correlation.State = "waiting";
            correlation.EditStateData = null;
            await _coreDb.SaveChangesAsync();

            if (sendButtons && tgConfig is not null)
            {
                try
                {
                    await _telegram.SendTextMessageWithOptionsAsync(tgConfig,
                        "¿Que quieres hacer ahora?", ControlOptions);
                }
                catch { /* non-critical */ }
            }
        }
    }
}
