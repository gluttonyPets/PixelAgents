using Microsoft.EntityFrameworkCore;
using Server.Data;

namespace Server.Services
{
    public interface ITenantDbContextFactory
    {
        UserDbContext Create(string dbName);
    }

    public class TenantDbContextFactory : ITenantDbContextFactory
    {
        private readonly IConfiguration _cfg;
        private readonly ILogger<TenantDbContextFactory> _log;

        public TenantDbContextFactory(IConfiguration cfg, ILogger<TenantDbContextFactory> log)
        {
            _cfg = cfg;
            _log = log;
        }

        public UserDbContext Create(string dbName)
        {
            var tpl = _cfg.GetConnectionString("TenantTemplate")!;
            var cs = tpl.Replace("{db}", dbName);
            var opts = new DbContextOptionsBuilder<UserDbContext>()
                .UseNpgsql(cs)
                .Options;
            var ctx = new UserDbContext(opts);
            ApplyPendingColumns(ctx, _log);
            return ctx;
        }

        private static void ApplyPendingColumns(UserDbContext ctx, ILogger log)
        {
            RunSafe(ctx, "ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"Context\" text", log);
            RunSafe(ctx, "ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"WhatsAppConfig\" text", log);
            RunSafe(ctx, "ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"TelegramConfig\" text", log);
            RunSafe(ctx, "ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"InstagramConfig\" text", log);
            RunSafe(ctx, "ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"TikTokConfig\" text", log);
            RunSafe(ctx, "ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"GraphLayout\" text", log);
            RunSafe(ctx, "ALTER TABLE \"ProjectExecutions\" ADD COLUMN IF NOT EXISTS \"PausedAtModuleId\" uuid", log);
            RunSafe(ctx, "ALTER TABLE \"ProjectExecutions\" DROP COLUMN IF EXISTS \"PausedAtStepOrder\"", log);
            RunSafe(ctx, "ALTER TABLE \"ProjectExecutions\" ADD COLUMN IF NOT EXISTS \"PausedStepData\" text", log);
            RunSafe(ctx, "ALTER TABLE \"ProjectExecutions\" ADD COLUMN IF NOT EXISTS \"UserInput\" text", log);
            RunSafe(ctx, @"
                CREATE TABLE IF NOT EXISTS ""ExecutionLogs"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""ExecutionId"" uuid NOT NULL REFERENCES ""ProjectExecutions""(""Id"") ON DELETE CASCADE,
                    ""Level"" varchar(20) NOT NULL DEFAULT 'info',
                    ""Message"" text NOT NULL,
                    ""ProjectModuleId"" uuid,
                    ""ModuleName"" varchar(200),
                    ""Timestamp"" timestamp with time zone NOT NULL
                )", log);
            RunSafe(ctx, @"ALTER TABLE ""ExecutionLogs"" ADD COLUMN IF NOT EXISTS ""ProjectModuleId"" uuid", log);
            RunSafe(ctx, @"ALTER TABLE ""ExecutionLogs"" ADD COLUMN IF NOT EXISTS ""ModuleName"" varchar(200)", log);
            RunSafe(ctx, @"ALTER TABLE ""ExecutionLogs"" DROP COLUMN IF EXISTS ""StepOrder""", log);
            RunSafe(ctx, @"ALTER TABLE ""ExecutionLogs"" DROP COLUMN IF EXISTS ""StepName""", log);
            RunSafe(ctx, @"
                CREATE INDEX IF NOT EXISTS ""IX_ExecutionLogs_ExecutionId""
                ON ""ExecutionLogs"" (""ExecutionId"")", log);
            RunSafe(ctx, "ALTER TABLE \"StepExecutions\" ADD COLUMN IF NOT EXISTS \"EstimatedCost\" numeric NOT NULL DEFAULT 0", log);
            RunSafe(ctx, @"DROP INDEX IF EXISTS ""IX_StepExecutions_ExecutionId_StepOrder""", log);
            RunSafe(ctx, @"ALTER TABLE ""StepExecutions"" DROP COLUMN IF EXISTS ""StepOrder""", log);
            RunSafe(ctx, @"CREATE INDEX IF NOT EXISTS ""IX_StepExecutions_ExecutionId"" ON ""StepExecutions"" (""ExecutionId"")", log);
            RunSafe(ctx, @"CREATE INDEX IF NOT EXISTS ""IX_StepExecutions_ExecutionId_ProjectModuleId"" ON ""StepExecutions"" (""ExecutionId"", ""ProjectModuleId"")", log);
            RunSafe(ctx, "ALTER TABLE \"ProjectExecutions\" ADD COLUMN IF NOT EXISTS \"TotalEstimatedCost\" numeric NOT NULL DEFAULT 0", log);
            RunSafe(ctx, "ALTER TABLE \"ProjectExecutions\" ADD COLUMN IF NOT EXISTS \"ExecutionSummary\" text", log);
            RunSafe(ctx, @"
                CREATE TABLE IF NOT EXISTS ""ProjectSchedules"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""ProjectId"" uuid NOT NULL REFERENCES ""Projects""(""Id"") ON DELETE CASCADE,
                    ""IsEnabled"" boolean NOT NULL DEFAULT true,
                    ""CronExpression"" varchar(100) NOT NULL,
                    ""TimeZone"" varchar(100) NOT NULL DEFAULT 'UTC',
                    ""UserInput"" text,
                    ""LastRunAt"" timestamp with time zone,
                    ""NextRunAt"" timestamp with time zone,
                    ""CreatedAt"" timestamp with time zone NOT NULL,
                    ""UpdatedAt"" timestamp with time zone NOT NULL
                )", log);
            RunSafe(ctx, @"CREATE INDEX IF NOT EXISTS ""IX_ProjectSchedules_ProjectId"" ON ""ProjectSchedules"" (""ProjectId"")", log);
            RunSafe(ctx, @"CREATE INDEX IF NOT EXISTS ""IX_ProjectSchedules_IsEnabled_NextRunAt"" ON ""ProjectSchedules"" (""IsEnabled"", ""NextRunAt"")", log);
            RunSafe(ctx, @"
                CREATE TABLE IF NOT EXISTS ""ModuleFiles"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""AiModuleId"" uuid NOT NULL REFERENCES ""AiModules""(""Id"") ON DELETE CASCADE,
                    ""FileName"" varchar(500) NOT NULL,
                    ""ContentType"" varchar(100) NOT NULL,
                    ""FilePath"" varchar(1000) NOT NULL,
                    ""FileSize"" bigint NOT NULL DEFAULT 0,
                    ""CreatedAt"" timestamp with time zone NOT NULL
                )", log);
            RunSafe(ctx, @"CREATE INDEX IF NOT EXISTS ""IX_ModuleFiles_AiModuleId"" ON ""ModuleFiles"" (""AiModuleId"")", log);
            // Drop legacy graph fields that are no longer part of the source model
            RunSafe(ctx, @"ALTER TABLE ""ProjectModules"" DROP COLUMN IF EXISTS ""BranchId""", log);
            RunSafe(ctx, @"ALTER TABLE ""ProjectModules"" DROP COLUMN IF EXISTS ""BranchFromStep""", log);
            RunSafe(ctx, @"ALTER TABLE ""ProjectExecutions"" DROP COLUMN IF EXISTS ""PausedBranches""", log);

            RunSafe(ctx, @"DROP INDEX IF EXISTS ""IX_ProjectModules_ProjectId_StepOrder""", log);
            RunSafe(ctx, @"DROP INDEX IF EXISTS ""IX_ProjectModules_ProjectId_BranchId_StepOrder""", log);
            RunSafe(ctx, @"ALTER TABLE ""ProjectModules"" DROP COLUMN IF EXISTS ""StepOrder""", log);
            RunSafe(ctx, @"ALTER TABLE ""ProjectModules"" DROP COLUMN IF EXISTS ""InputMapping""", log);
            RunSafe(ctx, @"CREATE INDEX IF NOT EXISTS ""IX_ProjectModules_ProjectId"" ON ""ProjectModules"" (""ProjectId"")", log);

            // ── Visual graph: node positions on ProjectModule ──
            RunSafe(ctx, @"ALTER TABLE ""ProjectModules"" ADD COLUMN IF NOT EXISTS ""PosX"" double precision NOT NULL DEFAULT 0", log);
            RunSafe(ctx, @"ALTER TABLE ""ProjectModules"" ADD COLUMN IF NOT EXISTS ""PosY"" double precision NOT NULL DEFAULT 0", log);

            // ── Visual graph: dedicated connections table ──
            RunSafe(ctx, @"
                CREATE TABLE IF NOT EXISTS ""ModuleConnections"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""ProjectId"" uuid NOT NULL REFERENCES ""Projects""(""Id"") ON DELETE CASCADE,
                    ""FromModuleId"" uuid NOT NULL REFERENCES ""ProjectModules""(""Id"") ON DELETE CASCADE,
                    ""FromPort"" varchar(100) NOT NULL,
                    ""ToModuleId"" uuid NOT NULL REFERENCES ""ProjectModules""(""Id"") ON DELETE CASCADE,
                    ""ToPort"" varchar(100) NOT NULL,
                    ""CreatedAt"" timestamp with time zone NOT NULL
                )", log);
            RunSafe(ctx, @"CREATE INDEX IF NOT EXISTS ""IX_ModuleConnections_ProjectId"" ON ""ModuleConnections"" (""ProjectId"")", log);
            RunSafe(ctx, @"CREATE INDEX IF NOT EXISTS ""IX_ModuleConnections_FromModuleId_FromPort"" ON ""ModuleConnections"" (""FromModuleId"", ""FromPort"")", log);
            RunSafe(ctx, @"CREATE INDEX IF NOT EXISTS ""IX_ModuleConnections_ToModuleId_ToPort"" ON ""ModuleConnections"" (""ToModuleId"", ""ToPort"")", log);
            RunSafe(ctx, @"ALTER TABLE ""ModuleConnections"" ADD COLUMN IF NOT EXISTS ""Format"" text", log);

            // ── Orchestrator outputs table ──
            RunSafe(ctx, @"
                CREATE TABLE IF NOT EXISTS ""OrchestratorOutputs"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""ProjectModuleId"" uuid NOT NULL REFERENCES ""ProjectModules""(""Id"") ON DELETE CASCADE,
                    ""OutputKey"" varchar(100) NOT NULL,
                    ""Label"" varchar(500) NOT NULL,
                    ""Prompt"" text NOT NULL,
                    ""SortOrder"" integer NOT NULL DEFAULT 0,
                    ""TargetModuleId"" uuid REFERENCES ""AiModules""(""Id"") ON DELETE SET NULL,
                    ""CreatedAt"" timestamp with time zone NOT NULL,
                    ""UpdatedAt"" timestamp with time zone NOT NULL
                )", log);
            RunSafe(ctx, @"CREATE INDEX IF NOT EXISTS ""IX_OrchestratorOutputs_ProjectModuleId"" ON ""OrchestratorOutputs"" (""ProjectModuleId"")", log);
            RunSafe(ctx, @"ALTER TABLE ""OrchestratorOutputs"" ADD COLUMN IF NOT EXISTS ""DataType"" varchar(50) NOT NULL DEFAULT 'text'", log);
            RunSafe(ctx, @"
                CREATE TABLE IF NOT EXISTS ""Rules"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""Title"" varchar(200) NOT NULL,
                    ""Content"" text NOT NULL,
                    ""IsActive"" boolean NOT NULL DEFAULT true,
                    ""SortOrder"" integer NOT NULL DEFAULT 0,
                    ""CreatedAt"" timestamp with time zone NOT NULL,
                    ""UpdatedAt"" timestamp with time zone NOT NULL
                )", log);
            RunSafe(ctx, @"CREATE INDEX IF NOT EXISTS ""IX_Rules_IsActive_SortOrder"" ON ""Rules"" (""IsActive"", ""SortOrder"")", log);
        }

        private static void RunSafe(UserDbContext ctx, string sql, ILogger? log = null)
        {
            try { ctx.Database.ExecuteSqlRaw(sql); }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "ApplyPendingColumns failed: {Sql}",
                    sql.Length > 80 ? sql[..80] : sql);
            }
        }
    }
}
