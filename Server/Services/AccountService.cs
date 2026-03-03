using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Server.Data;
using Server.Models;
using System.Security.Claims;

namespace Server.Services
{
    public class AccountService : IAccountService
    {
        private readonly CoreDbContext _core;
        private readonly UserManager<ApplicationUser> _um;
        private readonly IConfiguration _cfg;

        public AccountService(CoreDbContext core, UserManager<ApplicationUser> um, IConfiguration cfg)
        {
            _core = core;
            _um = um;
            _cfg = cfg;
        }

        public async Task RegisterAsync(string email, string password)
        {
            // Check if user already exists
            var existing = await _um.FindByEmailAsync(email);
            if (existing is not null)
                throw new Exception("El email ya esta registrado");

            var dbName = $"pixel_user_{Guid.NewGuid():N}";

            // Create tenant database
            var adminCs = _cfg.GetConnectionString("Core")!;
            await using (var adminConn = new NpgsqlConnection(adminCs))
            {
                await adminConn.OpenAsync();
                var sql = $@"CREATE DATABASE ""{dbName}"" TEMPLATE template0;";
                try
                {
                    await using var cmd = new NpgsqlCommand(sql, adminConn);
                    await cmd.ExecuteNonQueryAsync();
                }
                catch (PostgresException ex) when (ex.SqlState == "42P04")
                {
                    // database already exists, ignore
                }
            }

            // Initialize tenant schema
            var tpl = _cfg.GetConnectionString("TenantTemplate")!;
            var tenantCs = tpl.Replace("{db}", dbName);
            var opts = new DbContextOptionsBuilder<UserDbContext>()
                .UseNpgsql(tenantCs)
                .Options;
            await using (var tenantCtx = new UserDbContext(opts))
            {
                await tenantCtx.Database.EnsureCreatedAsync();
            }

            // Create account in core DB
            var acc = new Account { Id = Guid.NewGuid(), Name = email, DbName = dbName };
            _core.Accounts.Add(acc);
            await _core.SaveChangesAsync();

            // Create Identity user
            var user = new ApplicationUser { UserName = email, Email = email, AccountId = acc.Id };
            var res = await _um.CreateAsync(user, password);
            if (!res.Succeeded)
                throw new Exception(string.Join("; ", res.Errors.Select(e => e.Description)));

            // Store tenant DB name as claim
            await _um.AddClaimAsync(user, new Claim("db_name", dbName));
        }
    }
}
