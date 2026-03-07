using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Server.Models;

namespace Server.Data
{
    public class CoreDbContext : IdentityDbContext<ApplicationUser>
    {
        public CoreDbContext(DbContextOptions<CoreDbContext> options) : base(options) { }
        public DbSet<Account> Accounts => Set<Account>();
        public DbSet<WhatsAppCorrelation> WhatsAppCorrelations => Set<WhatsAppCorrelation>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<WhatsAppCorrelation>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.TenantDbName).IsRequired().HasMaxLength(200);
                e.Property(x => x.RecipientNumber).IsRequired().HasMaxLength(50);
                e.HasIndex(x => new { x.RecipientNumber, x.IsResolved });
            });
        }
    }
}
