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
            try
            {
                ctx.Database.ExecuteSqlRaw(
                    "ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"Context\" text");
                ctx.Database.ExecuteSqlRaw(
                    "ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"WhatsAppConfig\" text");
                ctx.Database.ExecuteSqlRaw(
                    "ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"TelegramConfig\" text");
                ctx.Database.ExecuteSqlRaw(
                    "ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"InstagramConfig\" text");
                ctx.Database.ExecuteSqlRaw(
                    "ALTER TABLE \"ProjectExecutions\" ADD COLUMN IF NOT EXISTS \"PausedAtStepOrder\" integer");
                ctx.Database.ExecuteSqlRaw(
                    "ALTER TABLE \"ProjectExecutions\" ADD COLUMN IF NOT EXISTS \"PausedStepData\" text");
                ctx.Database.ExecuteSqlRaw(
                    "ALTER TABLE \"ProjectExecutions\" ADD COLUMN IF NOT EXISTS \"UserInput\" text");
                ctx.Database.ExecuteSqlRaw(@"
                    CREATE TABLE IF NOT EXISTS ""ExecutionLogs"" (
                        ""Id"" uuid NOT NULL PRIMARY KEY,
                        ""ExecutionId"" uuid NOT NULL REFERENCES ""ProjectExecutions""(""Id"") ON DELETE CASCADE,
                        ""Level"" varchar(20) NOT NULL DEFAULT 'info',
                        ""Message"" text NOT NULL,
                        ""StepOrder"" integer,
                        ""StepName"" varchar(200),
                        ""Timestamp"" timestamp with time zone NOT NULL
                    )");
                ctx.Database.ExecuteSqlRaw(@"
                    CREATE INDEX IF NOT EXISTS ""IX_ExecutionLogs_ExecutionId""
                    ON ""ExecutionLogs"" (""ExecutionId"")");
            }
            catch { /* column already exists or table doesn't exist yet */ }
        }
    }
}
