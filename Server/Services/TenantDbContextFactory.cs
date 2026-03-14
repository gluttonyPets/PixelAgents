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
            RunSafe(ctx, "ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"CanvaConfig\" text");
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
        }

        private static void RunSafe(UserDbContext ctx, string sql)
        {
            try { ctx.Database.ExecuteSqlRaw(sql); }
            catch { /* column/table already exists or doesn't exist yet */ }
        }
    }
}
