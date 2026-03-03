using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Server.Data;
using Server.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

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
            var dbName = $"user_{Guid.NewGuid():N}";

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
                    // database exists, ignore
                }
            }

            var tpl = _cfg.GetConnectionString("TenantTemplate")!;
            var tenantCs = tpl.Replace("{db}", dbName);
            var opts = new DbContextOptionsBuilder<UserDbContext>().UseNpgsql(tenantCs).Options;
            using (var tenantCtx = new UserDbContext(opts))
            {
                tenantCtx.Database.EnsureCreated();
                tenantCtx.Database.EnsureDeleted();  // ⚠ Borra la base de datos
                tenantCtx.Database.EnsureCreated(); // 🔄 Crea la base de datos y las tablas
            }

            var acc = new Account { Id = Guid.NewGuid(), Name = email, DbName = dbName };
            _core.Accounts.Add(acc);
            await _core.SaveChangesAsync();

            var user = new ApplicationUser { UserName = email, Email = email, AccountId = acc.Id };
            var res = await _um.CreateAsync(user, password);
            if (!res.Succeeded)
                throw new Exception(string.Join(";", res.Errors.Select(e => e.Description)));

            await _um.AddClaimAsync(user, new Claim("db_name", dbName));
        }
    }
}
