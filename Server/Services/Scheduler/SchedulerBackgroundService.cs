using Cronos;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Server.Data;
using Server.Models;
using Server.Services.Ai;
using Server.Services.Telegram;

namespace Server.Services.Scheduler
{
    public class SchedulerBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<SchedulerBackgroundService> _log;
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

        public SchedulerBackgroundService(IServiceProvider sp, ILogger<SchedulerBackgroundService> log)
        {
            _sp = sp;
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _log.LogInformation("SchedulerBackgroundService started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Scheduler tick failed");
                }

                await Task.Delay(CheckInterval, stoppingToken);
            }
        }

        private async Task TickAsync(CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            // Get all tenant DB names from CoreDb
            using var scope = _sp.CreateScope();
            var coreDb = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
            var tenants = await coreDb.Accounts.Select(a => a.DbName).ToListAsync(ct);

            var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();

            foreach (var dbName in tenants)
            {
                try
                {
                    await ProcessTenantAsync(factory, dbName, now, ct);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Scheduler error for tenant {Tenant}", dbName);
                }
            }
        }

        private async Task ProcessTenantAsync(
            ITenantDbContextFactory factory, string dbName, DateTime now, CancellationToken ct)
        {
            await using var db = factory.Create(dbName);

            List<ProjectSchedule> dueSchedules;
            try
            {
                dueSchedules = await db.ProjectSchedules
                    .Where(s => s.IsEnabled && s.NextRunAt != null && s.NextRunAt <= now)
                    .ToListAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "42P01")
            {
                // Table doesn't exist yet for this tenant — skip
                return;
            }

            if (dueSchedules.Count == 0) return;

            _log.LogInformation("Tenant {Tenant}: {Count} schedule(s) due at {Now:O}",
                dbName, dueSchedules.Count, now);

            foreach (var schedule in dueSchedules)
            {
                // Claim the schedule by advancing NextRunAt before running the
                // pipeline. This prevents the same trigger from being picked up
                // again by:
                //   - The next tick if the pipeline runs long enough that the
                //     "now" captured at the start of this tick is stale.
                //   - A second scheduler instance during a rolling deploy.
                // It also guarantees that the next occurrence (e.g. 18:00 when
                // 15:00 just fired) is never skipped because we don't depend on
                // a single "now" value reaching the next slot.
                var previousNextRunAt = schedule.NextRunAt;
                // Advance from the previous slot so consecutive slots (e.g. 15:00
                // and 18:00) are never skipped when "now" already passed both. If
                // the previous slot is more than 1 hour stale (server downtime,
                // schedule re-enabled after being paused) skip ahead to "now" so
                // we don't backfill an unbounded number of past occurrences.
                var advanceFrom = previousNextRunAt is { } prev && now - prev <= TimeSpan.FromHours(1)
                    ? prev
                    : now;
                var nextRun = ComputeNextRun(schedule.CronExpression, schedule.TimeZone, advanceFrom);
                schedule.LastRunAt = now;
                schedule.NextRunAt = nextRun;
                schedule.UpdatedAt = now;

                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception saveEx)
                {
                    _log.LogError(saveEx,
                        "Failed to persist NextRunAt for schedule {Id} (project {ProjectId}); skipping this run",
                        schedule.Id, schedule.ProjectId);
                    continue;
                }

                _log.LogInformation(
                    "Schedule {Id} (project {ProjectId}) claimed. cron='{Cron}' tz='{Tz}' prevNextRunAt={Prev:O} newNextRunAt={Next:O}",
                    schedule.Id, schedule.ProjectId,
                    schedule.CronExpression, schedule.TimeZone,
                    previousNextRunAt, nextRun);

                try
                {
                    // Decide UserInput: prompt queue takes priority when enabled
                    string? effectiveInput = schedule.UserInput;
                    PlannedPrompt? consumedPrompt = null;

                    if (schedule.UsePromptQueue)
                    {
                        consumedPrompt = await db.PlannedPrompts
                            .Where(p => p.ProjectId == schedule.ProjectId && p.Status == PlannedPromptStatus.Pending)
                            .OrderBy(p => p.OrderIndex)
                            .ThenBy(p => p.CreatedAt)
                            .FirstOrDefaultAsync(ct);

                        if (consumedPrompt is not null)
                        {
                            effectiveInput = consumedPrompt.Content;
                        }
                        else
                        {
                            _log.LogInformation(
                                "Schedule {Id}: prompt queue empty for project {ProjectId}, falling back to static UserInput",
                                schedule.Id, schedule.ProjectId);
                        }
                    }

                    // Planner enabled ("usar planificador") but there is no prompt to run:
                    // no pending planned prompt and no static UserInput fallback. Instead of
                    // running the pipeline with an empty initial prompt, ask the user (via
                    // Telegram) to create a new planning and skip this scheduled run.
                    if (schedule.UsePromptQueue && string.IsNullOrWhiteSpace(effectiveInput))
                    {
                        var requested = await TryRequestPlanningAsync(db, schedule, dbName, ct);
                        if (requested)
                        {
                            _log.LogInformation(
                                "Schedule {Id}: no prompt available for project {ProjectId}, requested a new planning via Telegram; skipping run",
                                schedule.Id, schedule.ProjectId);
                            continue;
                        }

                        _log.LogWarning(
                            "Schedule {Id}: no prompt available for project {ProjectId} and no Telegram connection to request a planning",
                            schedule.Id, schedule.ProjectId);
                    }

                    // Execute pipeline
                    using var execScope = _sp.CreateScope();
                    var executor = execScope.ServiceProvider.GetRequiredService<IPipelineExecutor>();
                    await using var execDb = factory.Create(dbName);

                    await executor.ExecuteAsync(schedule.ProjectId, effectiveInput, execDb, dbName, ct, schedule.UseHistory);

                    if (consumedPrompt is not null)
                    {
                        consumedPrompt.Status = PlannedPromptStatus.Used;
                        consumedPrompt.UsedAt = DateTime.UtcNow;
                        consumedPrompt.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(ct);
                    }

                    _log.LogInformation("Schedule {Id} executed for project {ProjectId}{Queue}",
                        schedule.Id, schedule.ProjectId,
                        consumedPrompt is null ? "" : $" (consumed prompt {consumedPrompt.Id})");
                }
                catch (TransientProviderException ex)
                {
                    var retryAt = now.AddMinutes(30);
                    _log.LogWarning(ex,
                        "Schedule {Id} (project {ProjectId}): transient provider failure, rescheduling in 30 min at {RetryAt:O}",
                        schedule.Id, schedule.ProjectId, retryAt);
                    schedule.NextRunAt = retryAt;
                    schedule.UpdatedAt = now;
                    try { await db.SaveChangesAsync(ct); }
                    catch (Exception saveEx)
                    {
                        _log.LogError(saveEx,
                            "Failed to persist retry NextRunAt for schedule {Id}", schedule.Id);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to execute schedule {Id} for project {ProjectId}",
                        schedule.Id, schedule.ProjectId);
                    // NextRunAt was already advanced above, so we don't get stuck.
                }
            }
        }

        /// <summary>
        /// When a scheduled run driven by the planner ("usar planificador") has no prompt to
        /// execute, notify the project's Telegram chat and open an "awaiting_planning"
        /// correlation so the user can describe a new planning from the chat. Returns true when
        /// the request was sent (or one is already pending), meaning the run should be skipped.
        /// </summary>
        private async Task<bool> TryRequestPlanningAsync(
            UserDbContext db, ProjectSchedule schedule, string dbName, CancellationToken ct)
        {
            var project = await db.Projects
                .Include(p => p.TelegramConnection)
                .FirstOrDefaultAsync(p => p.Id == schedule.ProjectId, ct);

            if (project?.TelegramConnection is null)
                return false;

            var config = new TelegramConfig
            {
                BotToken = project.TelegramConnection.BotToken,
                ChatId = project.TelegramConnection.ChatId,
            };

            using var scope = _sp.CreateScope();
            var coreDb = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
            var telegram = scope.ServiceProvider.GetRequiredService<TelegramService>();

            // Avoid stacking multiple planning requests for the same project: if one is still
            // pending, treat it as handled so we don't run an empty pipeline in the meantime.
            var alreadyAsking = await coreDb.TelegramCorrelations
                .AnyAsync(c => !c.IsResolved
                    && c.State == "awaiting_planning"
                    && c.ProjectId == schedule.ProjectId, ct);
            if (alreadyAsking)
                return true;

            var correlation = new TelegramCorrelation
            {
                Id = Guid.NewGuid(),
                ExecutionId = Guid.Empty,
                ProjectModuleId = Guid.Empty,
                ProjectId = schedule.ProjectId,
                TenantDbName = dbName,
                ChatId = config.ChatId,
                CreatedAt = DateTime.UtcNow,
                IsResolved = false,
                State = "awaiting_planning",
            };
            coreDb.TelegramCorrelations.Add(correlation);
            await coreDb.SaveChangesAsync(ct);

            var msg =
                $"📭 No quedan temáticas planificadas para \"{project.Name}\".\n\n" +
                "Responde a este mensaje describiendo qué temas quieres generar y crearé una nueva " +
                "planificación. También puedes enviar varios prompts, uno por línea.";

            try
            {
                await telegram.SendTextMessageAsync(config, msg);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Failed to send planning request for project {ProjectId}; discarding correlation",
                    schedule.ProjectId);
                correlation.IsResolved = true;
                await coreDb.SaveChangesAsync(ct);
                return false;
            }

            return true;
        }

        public static DateTime? ComputeNextRun(string cronExpression, string timeZone, DateTime utcNow)
        {
            try
            {
                var cron = CronExpression.Parse(cronExpression);
                var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
                return cron.GetNextOccurrence(utcNow, tz);
            }
            catch
            {
                return null;
            }
        }
    }
}
