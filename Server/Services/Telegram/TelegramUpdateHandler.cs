using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Server.Data;
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

        public TelegramUpdateHandler(
            CoreDbContext coreDb,
            ITenantDbContextFactory factory,
            IPipelineExecutor executor,
            TelegramService telegram)
        {
            _coreDb = coreDb;
            _factory = factory;
            _executor = executor;
            _telegram = telegram;
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

            var correlation = await _coreDb.TelegramCorrelations
                .Where(c => !c.IsResolved && c.ChatId == normalizedChatId && c.State != "queued")
                .OrderBy(c => c.CreatedAt) // FIFO: oldest unresolved first
                .FirstOrDefaultAsync();

            if (correlation is null)
            {
                var pending = await _coreDb.TelegramCorrelations
                    .Where(c => !c.IsResolved)
                    .Select(c => new { c.ChatId, c.ExecutionId, c.CreatedAt })
                    .ToListAsync();
                Console.WriteLine($"[TG-Update] No correlation found for chatId={normalizedChatId}. Pending: {JsonSerializer.Serialize(pending)}");
                return;
            }

            // Reject text messages sent before the correlation was created (prevents stale "ok" from previous pipelines)
            // Callback queries (button presses) are exempt — they are always intentional actions on our messages.
            if (messageDate.HasValue && string.IsNullOrWhiteSpace(callbackQueryId))
            {
                // Allow a small tolerance (5 seconds) to handle clock skew between Telegram servers and our server
                var tolerance = TimeSpan.FromSeconds(5);
                if (messageDate.Value < correlation.CreatedAt - tolerance)
                {
                    Console.WriteLine($"[TG-Update] Ignored stale message: messageDate={messageDate.Value:O} < correlation.CreatedAt={correlation.CreatedAt:O}");
                    return;
                }
            }

            Console.WriteLine($"[TG-Update] Matched correlation {correlation.Id} for execution {correlation.ExecutionId}");

            await using var db = _factory.Create(correlation.TenantDbName);

            async Task<TelegramConfig?> GetTgConfigAsync()
            {
                var exec = await db.ProjectExecutions.FindAsync(correlation.ExecutionId);
                if (exec is null) return null;
                var proj = await db.Projects.FindAsync(exec.ProjectId);
                if (proj?.TelegramConfig is null) return null;
                return JsonSerializer.Deserialize<TelegramConfig>(proj.TelegramConfig);
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

                // State: awaiting_restart
                if (correlation.State == "awaiting_restart")
                {
                    var clarification = text.Trim().ToLowerInvariant() == "ok" ? null : text.Trim();

                    var execForRestart = await db.ProjectExecutions.FindAsync(correlation.ExecutionId);
                    var originalInput = "";
                    Guid? projectIdForRestart = execForRestart?.ProjectId;

                    if (execForRestart?.PausedStepData is not null)
                    {
                        try
                        {
                            var pauseDoc = JsonDocument.Parse(execForRestart.PausedStepData);
                            if (pauseDoc.RootElement.TryGetProperty("UserInput", out var uiProp))
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

                // Default: resume pipeline (branch-aware)
                if (!string.IsNullOrWhiteSpace(correlation.BranchId))
                {
                    await _executor.ResumeFromBranchInteractionAsync(
                        correlation.ExecutionId, correlation.BranchId, text, db, correlation.TenantDbName);
                }
                else
                {
                    await _executor.ResumeFromInteractionAsync(correlation.ExecutionId, text, db, correlation.TenantDbName);
                }
                correlation.IsResolved = true;
                await _coreDb.SaveChangesAsync();

                // Send next queued interaction message if any
                await _executor.SendNextQueuedInteractionAsync(correlation.ExecutionId, normalizedChatId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TG-Update] ERROR processing update for correlation {correlation.Id}: {ex}");

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
    }
}
