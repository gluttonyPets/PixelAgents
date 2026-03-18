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

        public TenantDbContextFactory(IConfiguration cfg)
        {
            _cfg = cfg;
        }

        public UserDbContext Create(string dbName)
        {
            var tpl = _cfg.GetConnectionString("TenantTemplate")!;
            var cs = tpl.Replace("{db}", dbName);
            var opts = new DbContextOptionsBuilder<UserDbContext>()
                .UseNpgsql(cs)
                .Options;
            var ctx = new UserDbContext(opts);
            ApplyPendingColumns(ctx);
            return ctx;
        }

        private static void ApplyPendingColumns(UserDbContext ctx)
        {
            RunSafe(ctx, "ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"Context\" text");
            RunSafe(ctx, "ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"WhatsAppConfig\" text");
            RunSafe(ctx, "ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"TelegramConfig\" text");
            RunSafe(ctx, "ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"InstagramConfig\" text");
            RunSafe(ctx, "ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"GraphLayout\" text");
            RunSafe(ctx, "ALTER TABLE \"ProjectExecutions\" ADD COLUMN IF NOT EXISTS \"PausedAtStepOrder\" integer");
            RunSafe(ctx, "ALTER TABLE \"ProjectExecutions\" ADD COLUMN IF NOT EXISTS \"PausedStepData\" text");
            RunSafe(ctx, "ALTER TABLE \"ProjectExecutions\" ADD COLUMN IF NOT EXISTS \"UserInput\" text");
            RunSafe(ctx, @"
                CREATE TABLE IF NOT EXISTS ""ExecutionLogs"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""ExecutionId"" uuid NOT NULL REFERENCES ""ProjectExecutions""(""Id"") ON DELETE CASCADE,
                    ""Level"" varchar(20) NOT NULL DEFAULT 'info',
                    ""Message"" text NOT NULL,
                    ""StepOrder"" integer,
                    ""StepName"" varchar(200),
                    ""Timestamp"" timestamp with time zone NOT NULL
                )");
            RunSafe(ctx, @"
                CREATE INDEX IF NOT EXISTS ""IX_ExecutionLogs_ExecutionId""
                ON ""ExecutionLogs"" (""ExecutionId"")");
            RunSafe(ctx, "ALTER TABLE \"StepExecutions\" ADD COLUMN IF NOT EXISTS \"EstimatedCost\" numeric NOT NULL DEFAULT 0");
            RunSafe(ctx, "ALTER TABLE \"ProjectExecutions\" ADD COLUMN IF NOT EXISTS \"TotalEstimatedCost\" numeric NOT NULL DEFAULT 0");
            RunSafe(ctx, "ALTER TABLE \"ProjectExecutions\" ADD COLUMN IF NOT EXISTS \"ExecutionSummary\" text");
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
                )");
            RunSafe(ctx, @"CREATE INDEX IF NOT EXISTS ""IX_ProjectSchedules_ProjectId"" ON ""ProjectSchedules"" (""ProjectId"")");
            RunSafe(ctx, @"CREATE INDEX IF NOT EXISTS ""IX_ProjectSchedules_IsEnabled_NextRunAt"" ON ""ProjectSchedules"" (""IsEnabled"", ""NextRunAt"")");
            RunSafe(ctx, @"
                CREATE TABLE IF NOT EXISTS ""ModuleFiles"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""AiModuleId"" uuid NOT NULL REFERENCES ""AiModules""(""Id"") ON DELETE CASCADE,
                    ""FileName"" varchar(500) NOT NULL,
                    ""ContentType"" varchar(100) NOT NULL,
                    ""FilePath"" varchar(1000) NOT NULL,
                    ""FileSize"" bigint NOT NULL DEFAULT 0,
                    ""CreatedAt"" timestamp with time zone NOT NULL
                )");
            RunSafe(ctx, @"CREATE INDEX IF NOT EXISTS ""IX_ModuleFiles_AiModuleId"" ON ""ModuleFiles"" (""AiModuleId"")");

            // ── Pipeline branching support ──
            RunSafe(ctx, @"ALTER TABLE ""ProjectModules"" ADD COLUMN IF NOT EXISTS ""BranchId"" varchar(100) NOT NULL DEFAULT 'main'");
            RunSafe(ctx, @"ALTER TABLE ""ProjectModules"" ADD COLUMN IF NOT EXISTS ""BranchFromStep"" integer");
            // ── Branch interaction pause support ──
            RunSafe(ctx, @"ALTER TABLE ""ProjectExecutions"" ADD COLUMN IF NOT EXISTS ""PausedBranches"" text");

            RunSafe(ctx, @"DROP INDEX IF EXISTS ""IX_ProjectModules_ProjectId_StepOrder""");
            RunSafe(ctx, @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ProjectModules_ProjectId_BranchId_StepOrder"" ON ""ProjectModules"" (""ProjectId"", ""BranchId"", ""StepOrder"")");
        }

        private static void RunSafe(UserDbContext ctx, string sql)
        {
            try { ctx.Database.ExecuteSqlRaw(sql); }
            catch { /* column/table already exists or doesn't exist yet */ }
        }
    }
}
