using Cronos;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;
using Server.Services.Ai;

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

            var dueSchedules = await db.ProjectSchedules
                .Where(s => s.IsEnabled && s.NextRunAt != null && s.NextRunAt <= now)
                .ToListAsync(ct);

            if (dueSchedules.Count == 0) return;

            _log.LogInformation("Tenant {Tenant}: {Count} schedule(s) due", dbName, dueSchedules.Count);

            foreach (var schedule in dueSchedules)
            {
                try
                {
                    // Execute pipeline
                    using var execScope = _sp.CreateScope();
                    var executor = execScope.ServiceProvider.GetRequiredService<IPipelineExecutor>();
                    await using var execDb = factory.Create(dbName);

                    await executor.ExecuteAsync(schedule.ProjectId, schedule.UserInput, execDb, dbName, ct);

                    // Update schedule times
                    schedule.LastRunAt = now;
                    schedule.NextRunAt = ComputeNextRun(schedule.CronExpression, schedule.TimeZone, now);
                    schedule.UpdatedAt = now;

                    _log.LogInformation("Schedule {Id} executed for project {ProjectId}",
                        schedule.Id, schedule.ProjectId);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to execute schedule {Id} for project {ProjectId}",
                        schedule.Id, schedule.ProjectId);

                    // Still advance NextRunAt so we don't get stuck retrying
                    schedule.NextRunAt = ComputeNextRun(schedule.CronExpression, schedule.TimeZone, now);
                    schedule.UpdatedAt = now;
                }
            }

            await db.SaveChangesAsync(ct);
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
