using Microsoft.EntityFrameworkCore;
using Npgsql;
using Server.Data;
using Server.Services.Ai;

namespace Server.Services.Scheduler
{
    /// <summary>
    /// Una vez al dia reanuda las ejecuciones que se quedaron en "WaitingForInput" y ya no
    /// tienen ninguna interaccion de Telegram abierta (la correlacion se cerro, p. ej. porque
    /// el "Continuar" fallo o el proceso se reinicio a mitad de la reanudacion). Las que
    /// todavia tienen una pregunta viva esperando al usuario no se tocan.
    /// </summary>
    public class StaleInteractionResumeService : BackgroundService
    {
        private const string DailyCron = "0 9 * * *";
        private const string DailyTimeZone = "Europe/Madrid";
        /// <summary>Margen para no pisar una reanudacion en curso ni una espera reciente.</summary>
        private static readonly TimeSpan MinAge = TimeSpan.FromHours(12);

        private readonly IServiceProvider _sp;
        private readonly ILogger<StaleInteractionResumeService> _log;

        public StaleInteractionResumeService(IServiceProvider sp, ILogger<StaleInteractionResumeService> log)
        {
            _sp = sp;
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var next = SchedulerBackgroundService.ComputeNextRun(DailyCron, DailyTimeZone, DateTime.UtcNow)
                           ?? DateTime.UtcNow.AddHours(24);
                var delay = next - DateTime.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, stoppingToken);

                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Stale interaction resume run failed");
                }
            }
        }

        public async Task RunOnceAsync(CancellationToken ct)
        {
            using var scope = _sp.CreateScope();
            var coreDb = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
            var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
            var tenants = await coreDb.Accounts.Select(a => a.DbName).ToListAsync(ct);
            var cutoff = DateTime.UtcNow - MinAge;

            foreach (var dbName in tenants)
            {
                try
                {
                    await using var db = factory.Create(dbName);

                    List<Guid> waiting;
                    try
                    {
                        waiting = await db.ProjectExecutions
                            .Where(e => e.Status == "WaitingForInput" && e.CreatedAt <= cutoff
                                && e.Project.DeletedAt == null)
                            .Select(e => e.Id)
                            .ToListAsync(ct);
                    }
                    catch (PostgresException ex) when (ex.SqlState == "42P01")
                    {
                        continue; // el tenant aun no tiene las tablas
                    }

                    if (waiting.Count == 0) continue;

                    var withOpenQuestion = await coreDb.TelegramCorrelations
                        .Where(c => !c.IsResolved && c.TenantDbName == dbName && waiting.Contains(c.ExecutionId))
                        .Select(c => c.ExecutionId)
                        .ToListAsync(ct);

                    foreach (var executionId in waiting.Except(withOpenQuestion))
                    {
                        await ResumeAsync(factory, dbName, executionId, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Stale interaction scan failed for tenant {Tenant}", dbName);
                }
            }
        }

        private async Task ResumeAsync(ITenantDbContextFactory factory, string dbName, Guid executionId, CancellationToken ct)
        {
            try
            {
                _log.LogInformation("Resuming stale WaitingForInput execution {ExecutionId} (tenant {Tenant})",
                    executionId, dbName);
                using var execScope = _sp.CreateScope();
                var executor = execScope.ServiceProvider.GetRequiredService<IPipelineExecutor>();
                await using var execDb = factory.Create(dbName);
                await executor.ResumeFromInteractionAsync(executionId, "continue", execDb, dbName, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Se reintentara en la siguiente pasada diaria.
                _log.LogError(ex, "Failed to resume stale execution {ExecutionId}", executionId);
            }
        }
    }
}
