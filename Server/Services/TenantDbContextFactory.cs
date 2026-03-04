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
            }
            catch { /* column already exists or table doesn't exist yet */ }
        }
    }
}
