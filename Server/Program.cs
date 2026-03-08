using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Server;
using Server.Data;
using Server.Models;
using Server.Services;
using Server.Services.Ai;
using Server.Hubs;

var builder = WebApplication.CreateBuilder(args);

// --- Database ---
builder.Services.AddDbContext<CoreDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Core")));

// --- Identity ---
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(opt =>
{
    opt.Password.RequireDigit = false;
    opt.Password.RequireUppercase = false;
    opt.Password.RequireNonAlphanumeric = false;
    opt.Password.RequiredLength = 6;
    opt.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<CoreDbContext>()
.AddDefaultTokenProviders();

// --- Cookie auth ---
builder.Services.ConfigureApplicationCookie(opt =>
{
    opt.Cookie.HttpOnly = true;
    opt.Cookie.SameSite = SameSiteMode.None;
    opt.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    opt.Events.OnRedirectToLogin = ctx =>
    {
        ctx.Response.StatusCode = 401;
        return Task.CompletedTask;
    };
    opt.Events.OnRedirectToAccessDenied = ctx =>
    {
        ctx.Response.StatusCode = 403;
        return Task.CompletedTask;
    };
});

// --- Services ---
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddSingleton<ITenantDbContextFactory, TenantDbContextFactory>();

// --- AI Providers ---
builder.Services.AddSingleton<IAiProvider, OpenAiProvider>();
builder.Services.AddSingleton<IAiProvider, AnthropicProvider>();
builder.Services.AddSingleton<IAiProvider, LeonardoProvider>();
builder.Services.AddSingleton<IAiProvider, GeminiProvider>();
builder.Services.AddSingleton<IAiProviderRegistry, AiProviderRegistry>();
builder.Services.AddHttpClient<Server.Services.WhatsApp.WhatsAppService>();
builder.Services.AddHttpClient<Server.Services.Telegram.TelegramService>();
builder.Services.AddTransient<IPipelineExecutor, PipelineExecutor>();
builder.Services.AddSingleton<ExecutionCancellationService>();
builder.Services.AddSingleton<IExecutionLogger, SignalRExecutionLogger>();
builder.Services.AddSignalR();

// --- CORS (Blazor Client) ---
builder.Services.AddCors(options =>
{
    options.AddPolicy("BlazorClient", policy =>
    {
        policy.SetIsOriginAllowed(origin =>
              {
                  var uri = new Uri(origin);
                  return uri.Host == "localhost" || uri.Host == "127.0.0.1";
              })
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// --- Auth middleware ---
builder.Services.AddAuthorization();

// --- Swagger ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseCors("BlazorClient");
app.UseAuthentication();
app.UseAuthorization();
app.UseSwagger();
app.UseSwaggerUI();

// --- Create core DB on startup ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
    db.Database.EnsureCreated();

    // Apply pending schema changes for existing core DBs
    try
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""WhatsAppCorrelations"" (
                ""Id"" uuid NOT NULL PRIMARY KEY,
                ""ExecutionId"" uuid NOT NULL,
                ""TenantDbName"" varchar(200) NOT NULL,
                ""RecipientNumber"" varchar(50) NOT NULL,
                ""StepOrder"" integer NOT NULL,
                ""CreatedAt"" timestamp with time zone NOT NULL,
                ""IsResolved"" boolean NOT NULL DEFAULT false
            )");
        db.Database.ExecuteSqlRaw(@"
            CREATE INDEX IF NOT EXISTS ""IX_WhatsAppCorrelations_RecipientNumber_IsResolved""
            ON ""WhatsAppCorrelations"" (""RecipientNumber"", ""IsResolved"")");

        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""TelegramCorrelations"" (
                ""Id"" uuid NOT NULL PRIMARY KEY,
                ""ExecutionId"" uuid NOT NULL,
                ""TenantDbName"" varchar(200) NOT NULL,
                ""ChatId"" varchar(50) NOT NULL,
                ""StepOrder"" integer NOT NULL,
                ""CreatedAt"" timestamp with time zone NOT NULL,
                ""IsResolved"" boolean NOT NULL DEFAULT false,
                ""State"" varchar(50) NOT NULL DEFAULT 'waiting'
            )");
        db.Database.ExecuteSqlRaw(@"
            CREATE INDEX IF NOT EXISTS ""IX_TelegramCorrelations_ChatId_IsResolved""
            ON ""TelegramCorrelations"" (""ChatId"", ""IsResolved"")");
        // Migration: add State column for existing tables
        db.Database.ExecuteSqlRaw(@"
            ALTER TABLE ""TelegramCorrelations"" ADD COLUMN IF NOT EXISTS ""State"" varchar(50) NOT NULL DEFAULT 'waiting'");
    }
    catch { }
}

// ==================== Helper: resolve tenant DB ====================

static async Task<UserDbContext?> ResolveTenantDb(
    HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory)
{
    var user = await um.GetUserAsync(ctx.User);
    if (user is null) return null;
    var claims = await um.GetClaimsAsync(user);
    var dbName = claims.FirstOrDefault(c => c.Type == "db_name")?.Value;
    if (dbName is null) return null;
    return factory.Create(dbName);
}

// ==================== Auth Endpoints ====================

app.MapPost("/api/auth/register", async (RegisterRequest req, IAccountService svc) =>
{
    try
    {
        await svc.RegisterAsync(req.Email, req.Password);
        return Results.Ok(new { message = "Usuario registrado correctamente" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/auth/login", async (
    LoginRequest req,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager) =>
{
    var user = await userManager.FindByEmailAsync(req.Email);
    if (user is null)
        return Results.Unauthorized();

    var result = await signInManager.PasswordSignInAsync(user, req.Password, req.RememberMe, false);
    if (!result.Succeeded)
        return Results.Unauthorized();

    var claims = await userManager.GetClaimsAsync(user);
    var dbName = claims.FirstOrDefault(c => c.Type == "db_name")?.Value;

    return Results.Ok(new AuthResponse(user.Email!, user.AccountId, dbName));
});

app.MapPost("/api/auth/logout", async (SignInManager<ApplicationUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.Ok(new { message = "Sesion cerrada" });
});

app.MapGet("/api/auth/me", async (
    HttpContext ctx,
    UserManager<ApplicationUser> userManager) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var user = await userManager.GetUserAsync(ctx.User);
    if (user is null)
        return Results.Unauthorized();

    var claims = await userManager.GetClaimsAsync(user);
    var dbName = claims.FirstOrDefault(c => c.Type == "db_name")?.Value;

    return Results.Ok(new AuthResponse(user.Email!, user.AccountId, dbName));
}).RequireAuthorization();

// ==================== ApiKey Endpoints ====================

app.MapPost("/api/apikeys", async (
    CreateApiKeyRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var apiKey = new ApiKey
    {
        Id = Guid.NewGuid(),
        Name = req.Name,
        ProviderType = req.ProviderType,
        EncryptedKey = req.Key,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    db.ApiKeys.Add(apiKey);
    await db.SaveChangesAsync();

    return Results.Created($"/api/apikeys/{apiKey.Id}",
        new ApiKeyResponse(apiKey.Id, apiKey.Name, apiKey.ProviderType, apiKey.CreatedAt));
}).RequireAuthorization();

app.MapGet("/api/apikeys", async (
    HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var keys = await db.ApiKeys
        .OrderByDescending(k => k.CreatedAt)
        .Select(k => new ApiKeyResponse(k.Id, k.Name, k.ProviderType, k.CreatedAt))
        .ToListAsync();

    return Results.Ok(keys);
}).RequireAuthorization();

app.MapGet("/api/apikeys/{id}", async (
    Guid id, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var key = await db.ApiKeys.FindAsync(id);
    if (key is null) return Results.NotFound();

    return Results.Ok(new ApiKeyResponse(key.Id, key.Name, key.ProviderType, key.CreatedAt));
}).RequireAuthorization();

app.MapPut("/api/apikeys/{id}", async (
    Guid id, UpdateApiKeyRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var key = await db.ApiKeys.FindAsync(id);
    if (key is null) return Results.NotFound();

    key.Name = req.Name;
    key.ProviderType = req.ProviderType;
    if (req.Key is not null) key.EncryptedKey = req.Key;
    key.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(new ApiKeyResponse(key.Id, key.Name, key.ProviderType, key.CreatedAt));
}).RequireAuthorization();

app.MapDelete("/api/apikeys/{id}", async (
    Guid id, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var key = await db.ApiKeys.FindAsync(id);
    if (key is null) return Results.NotFound();

    db.ApiKeys.Remove(key);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

// ==================== AiModule Endpoints ====================

app.MapPost("/api/modules", async (
    CreateAiModuleRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var module = new AiModule
    {
        Id = Guid.NewGuid(),
        Name = req.Name,
        Description = req.Description,
        ProviderType = req.ProviderType,
        ModuleType = req.ModuleType,
        ModelName = req.ModelName,
        ApiKeyId = req.ApiKeyId,
        Configuration = req.Configuration,
        IsEnabled = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    db.AiModules.Add(module);
    await db.SaveChangesAsync();

    return Results.Created($"/api/modules/{module.Id}",
        new AiModuleResponse(module.Id, module.Name, module.Description,
            module.ProviderType, module.ModuleType, module.ModelName,
            module.ApiKeyId, null, module.Configuration, module.IsEnabled,
            module.CreatedAt, module.UpdatedAt));
}).RequireAuthorization();

app.MapGet("/api/modules", async (
    string? providerType, string? moduleType,
    HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var query = db.AiModules.Include(m => m.ApiKey).AsQueryable();

    if (providerType is not null)
        query = query.Where(m => m.ProviderType == providerType);
    if (moduleType is not null)
        query = query.Where(m => m.ModuleType == moduleType);

    var modules = await query
        .OrderByDescending(m => m.CreatedAt)
        .Select(m => new AiModuleResponse(m.Id, m.Name, m.Description,
            m.ProviderType, m.ModuleType, m.ModelName,
            m.ApiKeyId, m.ApiKey != null ? m.ApiKey.Name : null,
            m.Configuration, m.IsEnabled, m.CreatedAt, m.UpdatedAt))
        .ToListAsync();

    return Results.Ok(modules);
}).RequireAuthorization();

app.MapGet("/api/modules/{id}", async (
    Guid id, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var m = await db.AiModules.Include(x => x.ApiKey).FirstOrDefaultAsync(x => x.Id == id);
    if (m is null) return Results.NotFound();

    return Results.Ok(new AiModuleResponse(m.Id, m.Name, m.Description,
        m.ProviderType, m.ModuleType, m.ModelName,
        m.ApiKeyId, m.ApiKey?.Name, m.Configuration, m.IsEnabled,
        m.CreatedAt, m.UpdatedAt));
}).RequireAuthorization();

app.MapPut("/api/modules/{id}", async (
    Guid id, UpdateAiModuleRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var m = await db.AiModules.FindAsync(id);
    if (m is null) return Results.NotFound();

    m.Name = req.Name;
    m.Description = req.Description;
    m.ProviderType = req.ProviderType;
    m.ModuleType = req.ModuleType;
    m.ModelName = req.ModelName;
    m.ApiKeyId = req.ApiKeyId;
    m.Configuration = req.Configuration;
    m.IsEnabled = req.IsEnabled;
    m.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(new AiModuleResponse(m.Id, m.Name, m.Description,
        m.ProviderType, m.ModuleType, m.ModelName,
        m.ApiKeyId, null, m.Configuration, m.IsEnabled,
        m.CreatedAt, m.UpdatedAt));
}).RequireAuthorization();

app.MapDelete("/api/modules/{id}", async (
    Guid id, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var m = await db.AiModules.FindAsync(id);
    if (m is null) return Results.NotFound();

    db.AiModules.Remove(m);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

// ==================== Project Endpoints ====================

app.MapPost("/api/projects", async (
    CreateProjectRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = new Project
    {
        Id = Guid.NewGuid(),
        Name = req.Name,
        Description = req.Description,
        Context = req.Context,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    db.Projects.Add(project);
    await db.SaveChangesAsync();

    return Results.Created($"/api/projects/{project.Id}",
        new ProjectResponse(project.Id, project.Name, project.Description, project.Context,
            project.CreatedAt, project.UpdatedAt));
}).RequireAuthorization();

app.MapGet("/api/projects", async (
    HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var projects = await db.Projects
        .OrderByDescending(p => p.CreatedAt)
        .Select(p => new ProjectResponse(p.Id, p.Name, p.Description, p.Context, p.CreatedAt, p.UpdatedAt))
        .ToListAsync();

    return Results.Ok(projects);
}).RequireAuthorization();

app.MapGet("/api/projects/{id}", async (
    Guid id, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects
        .Include(p => p.ProjectModules.OrderBy(pm => pm.StepOrder))
            .ThenInclude(pm => pm.AiModule)
        .FirstOrDefaultAsync(p => p.Id == id);

    if (project is null) return Results.NotFound();

    var modules = project.ProjectModules.Select(pm =>
        new ProjectModuleResponse(pm.Id, pm.AiModuleId, pm.AiModule.Name,
            pm.AiModule.ModuleType, pm.AiModule.ModelName, pm.StepOrder, pm.StepName,
            pm.InputMapping, pm.Configuration, pm.IsActive)).ToList();

    return Results.Ok(new ProjectDetailResponse(
        project.Id, project.Name, project.Description, project.Context,
        project.CreatedAt, project.UpdatedAt, modules));
}).RequireAuthorization();

app.MapPut("/api/projects/{id}", async (
    Guid id, UpdateProjectRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects.FindAsync(id);
    if (project is null) return Results.NotFound();

    project.Name = req.Name;
    project.Description = req.Description;
    project.Context = req.Context;
    project.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(new ProjectResponse(project.Id, project.Name, project.Description, project.Context,
        project.CreatedAt, project.UpdatedAt));
}).RequireAuthorization();

app.MapDelete("/api/projects/{id}", async (
    Guid id, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects.FindAsync(id);
    if (project is null) return Results.NotFound();

    db.Projects.Remove(project);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

// ==================== ProjectModule (Pipeline) Endpoints ====================

app.MapPost("/api/projects/{projectId}/modules", async (
    Guid projectId, AddProjectModuleRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects.FindAsync(projectId);
    if (project is null) return Results.NotFound();

    var module = await db.AiModules.FindAsync(req.AiModuleId);
    if (module is null) return Results.BadRequest(new { error = "Modulo no encontrado" });

    var pm = new ProjectModule
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        AiModuleId = req.AiModuleId,
        StepOrder = req.StepOrder,
        StepName = req.StepName,
        InputMapping = req.InputMapping,
        Configuration = req.Configuration,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    db.ProjectModules.Add(pm);
    await db.SaveChangesAsync();

    return Results.Created($"/api/projects/{projectId}/modules/{pm.Id}",
        new ProjectModuleResponse(pm.Id, pm.AiModuleId, module.Name,
            module.ModuleType, module.ModelName, pm.StepOrder, pm.StepName,
            pm.InputMapping, pm.Configuration, pm.IsActive));
}).RequireAuthorization();

app.MapPut("/api/projects/{projectId}/modules/{id}", async (
    Guid projectId, Guid id, UpdateProjectModuleRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var pm = await db.ProjectModules
        .Include(x => x.AiModule)
        .FirstOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId);
    if (pm is null) return Results.NotFound();

    pm.StepOrder = req.StepOrder;
    pm.StepName = req.StepName;
    pm.InputMapping = req.InputMapping;
    pm.Configuration = req.Configuration;
    pm.IsActive = req.IsActive;
    pm.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(new ProjectModuleResponse(pm.Id, pm.AiModuleId, pm.AiModule.Name,
        pm.AiModule.ModuleType, pm.AiModule.ModelName, pm.StepOrder, pm.StepName,
        pm.InputMapping, pm.Configuration, pm.IsActive));
}).RequireAuthorization();

app.MapPost("/api/projects/{projectId}/modules/swap", async (
    Guid projectId, SwapStepOrderRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var pmA = await db.ProjectModules.FirstOrDefaultAsync(x => x.Id == req.ModuleIdA && x.ProjectId == projectId);
    var pmB = await db.ProjectModules.FirstOrDefaultAsync(x => x.Id == req.ModuleIdB && x.ProjectId == projectId);
    if (pmA is null || pmB is null) return Results.NotFound();

    // Swap via temp value to avoid unique constraint violation
    var now = DateTime.UtcNow;
    var tempOrder = -1;
    var orderA = pmA.StepOrder;
    var orderB = pmB.StepOrder;

    await using var tx = await db.Database.BeginTransactionAsync();
    // Step 1: Move A to temp
    await db.Database.ExecuteSqlInterpolatedAsync(
        $@"UPDATE ""ProjectModules"" SET ""StepOrder"" = {tempOrder}, ""UpdatedAt"" = {now} WHERE ""Id"" = {pmA.Id}");
    // Step 2: Move B to A's old position
    await db.Database.ExecuteSqlInterpolatedAsync(
        $@"UPDATE ""ProjectModules"" SET ""StepOrder"" = {orderA}, ""UpdatedAt"" = {now} WHERE ""Id"" = {pmB.Id}");
    // Step 3: Move A to B's old position
    await db.Database.ExecuteSqlInterpolatedAsync(
        $@"UPDATE ""ProjectModules"" SET ""StepOrder"" = {orderB}, ""UpdatedAt"" = {now} WHERE ""Id"" = {pmA.Id}");
    await tx.CommitAsync();

    return Results.Ok();
}).RequireAuthorization();

app.MapDelete("/api/projects/{projectId}/modules/{id}", async (
    Guid projectId, Guid id, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var pm = await db.ProjectModules
        .FirstOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId);
    if (pm is null) return Results.NotFound();

    // Remove related StepExecutions (and their files via cascade) to avoid FK Restrict violation
    var stepExecutions = await db.StepExecutions
        .Where(se => se.ProjectModuleId == id)
        .ToListAsync();
    if (stepExecutions.Count > 0)
        db.StepExecutions.RemoveRange(stepExecutions);

    var removedOrder = pm.StepOrder;
    db.ProjectModules.Remove(pm);
    await db.SaveChangesAsync();

    // Renumber remaining steps so there are no gaps
    var remaining = await db.ProjectModules
        .Where(x => x.ProjectId == projectId && x.StepOrder > removedOrder)
        .OrderBy(x => x.StepOrder)
        .ToListAsync();
    foreach (var r in remaining)
        r.StepOrder--;
    if (remaining.Count > 0)
        await db.SaveChangesAsync();

    return Results.NoContent();
}).RequireAuthorization();

// ==================== Execution Endpoints ====================

app.MapPost("/api/projects/{projectId}/execute", async (
    Guid projectId, ExecuteProjectRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory,
    IPipelineExecutor executor, ExecutionCancellationService cancellation) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var user = await um.GetUserAsync(ctx.User);
    var claims = await um.GetClaimsAsync(user!);
    var tenantDbName = claims.First(c => c.Type == "db_name").Value;

    var ct = cancellation.Register(projectId);

    try
    {
        var execution = await executor.ExecuteAsync(projectId, req.UserInput, db, tenantDbName, ct);

        var exec = await db.ProjectExecutions
            .Include(e => e.StepExecutions.OrderBy(s => s.StepOrder))
                .ThenInclude(s => s.Files)
            .Include(e => e.StepExecutions)
                .ThenInclude(s => s.ProjectModule)
                    .ThenInclude(pm => pm.AiModule)
            .FirstAsync(e => e.Id == execution.Id);

        var steps = exec.StepExecutions.OrderBy(s => s.StepOrder).Select(s =>
            new StepExecutionResponse(s.Id, s.ProjectModuleId,
                s.ProjectModule.AiModule.Name, s.StepOrder,
                s.Status, s.InputData, s.OutputData, s.ErrorMessage,
                s.CreatedAt, s.CompletedAt,
                s.Files.Select(f => new ExecutionFileResponse(
                    f.Id, f.FileName, f.ContentType, f.FilePath,
                    f.Direction, f.FileSize, f.CreatedAt)).ToList()
            )).ToList();

        cancellation.Remove(projectId);

        return Results.Ok(new ExecutionDetailResponse(
            exec.Id, exec.ProjectId, exec.Status, exec.WorkspacePath,
            exec.CreatedAt, exec.CompletedAt, steps));
    }
    catch (Exception ex)
    {
        cancellation.Remove(projectId);
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapGet("/api/projects/{projectId}/executions", async (
    Guid projectId, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var executions = await db.ProjectExecutions
        .Where(e => e.ProjectId == projectId)
        .OrderByDescending(e => e.CreatedAt)
        .Select(e => new ExecutionResponse(e.Id, e.ProjectId, e.Status,
            e.WorkspacePath, e.CreatedAt, e.CompletedAt))
        .ToListAsync();

    return Results.Ok(executions);
}).RequireAuthorization();

app.MapGet("/api/executions/{id}", async (
    Guid id, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var exec = await db.ProjectExecutions
        .Include(e => e.StepExecutions.OrderBy(s => s.StepOrder))
            .ThenInclude(s => s.Files)
        .Include(e => e.StepExecutions)
            .ThenInclude(s => s.ProjectModule)
                .ThenInclude(pm => pm.AiModule)
        .FirstOrDefaultAsync(e => e.Id == id);

    if (exec is null) return Results.NotFound();

    var steps = exec.StepExecutions.OrderBy(s => s.StepOrder).Select(s =>
        new StepExecutionResponse(s.Id, s.ProjectModuleId,
            s.ProjectModule.AiModule.Name, s.StepOrder,
            s.Status, s.InputData, s.OutputData, s.ErrorMessage,
            s.CreatedAt, s.CompletedAt,
            s.Files.Select(f => new ExecutionFileResponse(
                f.Id, f.FileName, f.ContentType, f.FilePath,
                f.Direction, f.FileSize, f.CreatedAt)).ToList()
        )).ToList();

    return Results.Ok(new ExecutionDetailResponse(
        exec.Id, exec.ProjectId, exec.Status, exec.WorkspacePath,
        exec.CreatedAt, exec.CompletedAt, steps));
}).RequireAuthorization();

// Retry execution from a specific step
app.MapPost("/api/executions/{executionId}/retry-from-step", async (
    Guid executionId, RetryFromStepRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory,
    IPipelineExecutor executor, ExecutionCancellationService cancellation) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var user = await um.GetUserAsync(ctx.User);
    var userClaims = await um.GetClaimsAsync(user!);
    var tenantDbName = userClaims.First(c => c.Type == "db_name").Value;

    try
    {
        var projectId = await db.ProjectExecutions.Where(e => e.Id == executionId).Select(e => e.ProjectId).FirstAsync();
        var ct = cancellation.Register(projectId);
        var execution = await executor.RetryFromStepAsync(executionId, req.StepOrder, req.Comment, db, tenantDbName, ct);
        cancellation.Remove(projectId);

        var exec = await db.ProjectExecutions
            .Include(e => e.StepExecutions.OrderBy(s => s.StepOrder))
                .ThenInclude(s => s.Files)
            .Include(e => e.StepExecutions)
                .ThenInclude(s => s.ProjectModule)
                    .ThenInclude(pm => pm.AiModule)
            .FirstAsync(e => e.Id == execution.Id);

        var steps = exec.StepExecutions.OrderBy(s => s.StepOrder).Select(s =>
            new StepExecutionResponse(s.Id, s.ProjectModuleId,
                s.ProjectModule.AiModule.Name, s.StepOrder,
                s.Status, s.InputData, s.OutputData, s.ErrorMessage,
                s.CreatedAt, s.CompletedAt,
                s.Files.Select(f => new ExecutionFileResponse(
                    f.Id, f.FileName, f.ContentType, f.FilePath,
                    f.Direction, f.FileSize, f.CreatedAt)).ToList()
            )).ToList();

        return Results.Ok(new ExecutionDetailResponse(
            exec.Id, exec.ProjectId, exec.Status, exec.WorkspacePath,
            exec.CreatedAt, exec.CompletedAt, steps));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

// Cancel a running pipeline execution
app.MapPost("/api/projects/{projectId}/cancel", (
    Guid projectId, ExecutionCancellationService cancellation) =>
{
    var cancelled = cancellation.Cancel(projectId);
    return Results.Ok(new { cancelled });
}).RequireAuthorization();

// Download execution file
app.MapGet("/api/executions/{executionId}/files/{fileId}", async (
    Guid executionId, Guid fileId, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var exec = await db.ProjectExecutions
        .FirstOrDefaultAsync(e => e.Id == executionId);
    if (exec is null) return Results.NotFound();

    var file = await db.ExecutionFiles
        .FirstOrDefaultAsync(f => f.Id == fileId &&
            f.StepExecution.ExecutionId == executionId);
    if (file is null) return Results.NotFound();

    var fullPath = Path.Combine(exec.WorkspacePath, file.FilePath);
    if (!File.Exists(fullPath)) return Results.NotFound("Archivo no encontrado en disco");

    var bytes = await File.ReadAllBytesAsync(fullPath);
    return Results.File(bytes, file.ContentType, file.FileName);
}).RequireAuthorization();

// ==================== WhatsApp Config Endpoints ====================

app.MapGet("/api/projects/{projectId:guid}/whatsapp-config", async (
    Guid projectId, HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects.FindAsync(projectId);
    if (project is null) return Results.NotFound();

    if (string.IsNullOrWhiteSpace(project.WhatsAppConfig))
        return Results.Ok(new WhatsAppConfigDto("", "", "", ""));

    var config = System.Text.Json.JsonSerializer.Deserialize<WhatsAppConfigDto>(project.WhatsAppConfig);
    return Results.Ok(config);
}).RequireAuthorization();

app.MapPut("/api/projects/{projectId:guid}/whatsapp-config", async (
    Guid projectId, WhatsAppConfigDto dto, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects.FindAsync(projectId);
    if (project is null) return Results.NotFound();

    project.WhatsAppConfig = System.Text.Json.JsonSerializer.Serialize(dto);
    project.UpdatedAt = DateTime.UtcNow;

    // Ensure the WhatsApp Interaction sentinel module exists
    var hasWhatsAppInteraction = await db.AiModules.AnyAsync(m => m.ModuleType == "Interaction" && m.ModelName == "whatsapp");
    if (!hasWhatsAppInteraction)
    {
        db.AiModules.Add(new AiModule
        {
            Id = Guid.NewGuid(),
            Name = "WhatsApp Interaction",
            Description = "Pausa el pipeline y envia un mensaje a WhatsApp para obtener feedback del usuario.",
            ProviderType = "System",
            ModuleType = "Interaction",
            ModelName = "whatsapp",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    await db.SaveChangesAsync();

    return Results.Ok(new { message = "Configuracion WhatsApp guardada" });
}).RequireAuthorization();

// ==================== WhatsApp Webhook Endpoints ====================

app.MapGet("/api/webhooks/whatsapp", (HttpContext ctx) =>
{
    var hubMode = ctx.Request.Query["hub.mode"].FirstOrDefault();
    var hubToken = ctx.Request.Query["hub.verify_token"].FirstOrDefault();
    var hubChallenge = ctx.Request.Query["hub.challenge"].FirstOrDefault();

    // Find verify token from any project with WhatsApp config
    // For simplicity, we accept any valid verify token from any tenant
    var verifyToken = builder.Configuration["WhatsApp:WebhookVerifyToken"] ?? "pixelagents-webhook-verify";

    var (valid, challenge) = Server.Services.WhatsApp.WhatsAppService.VerifyWebhook(verifyToken, hubMode, hubToken, hubChallenge);
    return valid ? Results.Text(challenge!) : Results.StatusCode(403);
});

app.MapPost("/api/webhooks/whatsapp", async (
    HttpContext ctx, CoreDbContext coreDb, ITenantDbContextFactory factory, IPipelineExecutor executor) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    System.Text.Json.JsonElement json;
    try { json = System.Text.Json.JsonDocument.Parse(body).RootElement; }
    catch { return Results.Ok(); }

    var (text, senderPhone) = Server.Services.WhatsApp.WhatsAppService.ParseIncomingMessage(json);

    if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(senderPhone))
        return Results.Ok(); // Not a text message or status update — acknowledge

    // Find pending correlation
    var correlation = await coreDb.WhatsAppCorrelations
        .Where(c => !c.IsResolved && c.RecipientNumber == senderPhone)
        .OrderByDescending(c => c.CreatedAt)
        .FirstOrDefaultAsync();

    if (correlation is null)
        return Results.Ok(); // No pending interaction for this sender

    // Resolve tenant and resume pipeline
    await using var db = factory.Create(correlation.TenantDbName);

    try
    {
        await executor.ResumeFromInteractionAsync(correlation.ExecutionId, text, db, correlation.TenantDbName);
        correlation.IsResolved = true;
        await coreDb.SaveChangesAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error resuming pipeline: {ex.Message}");
    }

    return Results.Ok();
});

// ==================== Telegram Config Endpoints ====================

app.MapGet("/api/projects/{projectId:guid}/telegram-config", async (
    Guid projectId, HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects.FindAsync(projectId);
    if (project is null) return Results.NotFound();

    if (string.IsNullOrWhiteSpace(project.TelegramConfig))
        return Results.Ok(new TelegramConfigDto("", ""));

    var config = System.Text.Json.JsonSerializer.Deserialize<TelegramConfigDto>(project.TelegramConfig);
    return Results.Ok(config);
}).RequireAuthorization();

app.MapPut("/api/projects/{projectId:guid}/telegram-config", async (
    Guid projectId, TelegramConfigDto dto, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory,
    Server.Services.Telegram.TelegramService telegram) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects.FindAsync(projectId);
    if (project is null) return Results.NotFound();

    project.TelegramConfig = System.Text.Json.JsonSerializer.Serialize(dto);
    project.UpdatedAt = DateTime.UtcNow;

    // Ensure the Telegram Interaction sentinel module exists
    var hasTelegramInteraction = await db.AiModules.AnyAsync(m => m.ModuleType == "Interaction" && m.ModelName == "telegram");
    if (!hasTelegramInteraction)
    {
        db.AiModules.Add(new AiModule
        {
            Id = Guid.NewGuid(),
            Name = "Telegram Interaction",
            Description = "Pausa el pipeline y envia un mensaje a Telegram para obtener feedback del usuario.",
            ProviderType = "System",
            ModuleType = "Interaction",
            ModelName = "telegram",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    await db.SaveChangesAsync();

    // Register webhook with Telegram
    if (!string.IsNullOrWhiteSpace(dto.BotToken))
    {
        try
        {
            // Try configured base URL first, then auto-detect from the incoming request
            var baseUrl = builder.Configuration["Telegram:WebhookBaseUrl"]
                ?? builder.Configuration["BaseUrl"]
                ?? "";

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                // Auto-detect: use the scheme + host from the current request
                var req = ctx.Request;
                baseUrl = $"{req.Scheme}://{req.Host}";
            }

            var webhookUrl = $"{baseUrl.TrimEnd('/')}/api/webhooks/telegram";
            Console.WriteLine($"[Telegram] Registering webhook: {webhookUrl}");
            await telegram.SetWebhookAsync(dto.BotToken, webhookUrl);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not set Telegram webhook: {ex.Message}");
        }
    }

    return Results.Ok(new { message = "Configuracion Telegram guardada" });
}).RequireAuthorization();

// ==================== Telegram Webhook Endpoint ====================

app.MapPost("/api/webhooks/telegram", async (
    HttpContext ctx, CoreDbContext coreDb, ITenantDbContextFactory factory,
    IPipelineExecutor executor, Server.Services.Telegram.TelegramService telegram) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    System.Text.Json.JsonElement json;
    try { json = System.Text.Json.JsonDocument.Parse(body).RootElement; }
    catch { return Results.Ok(); }

    var (text, chatId, callbackQueryId) = Server.Services.Telegram.TelegramService.ParseIncomingUpdate(json);

    Console.WriteLine($"[TG-Webhook] Parsed update — text={text}, chatId={chatId}, callbackQueryId={callbackQueryId}");

    if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(chatId))
    {
        Console.WriteLine("[TG-Webhook] Ignored: text or chatId is empty");
        return Results.Ok();
    }

    // Find pending correlation — normalize chatId to match stored format
    var normalizedChatId = chatId.Trim();

    var correlation = await coreDb.TelegramCorrelations
        .Where(c => !c.IsResolved && c.ChatId == normalizedChatId)
        .OrderByDescending(c => c.CreatedAt)
        .FirstOrDefaultAsync();

    if (correlation is null)
    {
        // Log all pending correlations for debugging
        var pending = await coreDb.TelegramCorrelations
            .Where(c => !c.IsResolved)
            .Select(c => new { c.ChatId, c.ExecutionId, c.CreatedAt })
            .ToListAsync();
        Console.WriteLine($"[TG-Webhook] No correlation found for chatId={normalizedChatId}. Pending correlations: {System.Text.Json.JsonSerializer.Serialize(pending)}");
        return Results.Ok();
    }

    Console.WriteLine($"[TG-Webhook] Matched correlation {correlation.Id} for execution {correlation.ExecutionId}");

    await using var db = factory.Create(correlation.TenantDbName);

    // Helper: resolve TelegramConfig for this correlation
    async Task<Server.Services.Telegram.TelegramConfig?> GetTgConfigAsync()
    {
        var exec = await db.ProjectExecutions.FindAsync(correlation.ExecutionId);
        if (exec is null) return null;
        var proj = await db.Projects.FindAsync(exec.ProjectId);
        if (proj?.TelegramConfig is null) return null;
        return System.Text.Json.JsonSerializer.Deserialize<Server.Services.Telegram.TelegramConfig>(proj.TelegramConfig);
    }

    try
    {
        // Answer callback query (removes loading spinner from button)
        if (!string.IsNullOrWhiteSpace(callbackQueryId))
        {
            var tgConfig = await GetTgConfigAsync();
            if (tgConfig is not null)
            {
                try { await telegram.AnswerCallbackQueryAsync(tgConfig.BotToken, callbackQueryId); }
                catch { /* non-critical */ }
            }
        }

        // ── State: awaiting_restart → user is sending clarification text for restart ──
        if (correlation.State == "awaiting_restart")
        {
            var clarification = text.Trim().ToLowerInvariant() == "ok" ? null : text.Trim();

            // Recover original user input from PausedStepData before aborting
            var execForRestart = await db.ProjectExecutions.FindAsync(correlation.ExecutionId);
            var originalInput = "";
            Guid? projectIdForRestart = execForRestart?.ProjectId;

            if (execForRestart?.PausedStepData is not null)
            {
                try
                {
                    var pauseDoc = System.Text.Json.JsonDocument.Parse(execForRestart.PausedStepData);
                    if (pauseDoc.RootElement.TryGetProperty("UserInput", out var uiProp))
                        originalInput = uiProp.GetString() ?? "";
                }
                catch { }
            }

            // Abort current execution
            await executor.AbortFromInteractionAsync(correlation.ExecutionId, db, correlation.TenantDbName);

            if (projectIdForRestart is not null)
            {
                var restartInput = string.IsNullOrWhiteSpace(clarification)
                    ? originalInput
                    : $"{originalInput}\n\nAclaracion del usuario: {clarification}";

                // Re-execute from step 1
                await executor.ExecuteAsync(projectIdForRestart.Value, restartInput, db, correlation.TenantDbName);

                // Notify user
                var tgConfig = await GetTgConfigAsync();
                if (tgConfig is not null)
                {
                    var restartMsg = string.IsNullOrWhiteSpace(clarification)
                        ? "🔄 Pipeline reiniciado."
                        : $"🔄 Pipeline reiniciado con aclaracion: \"{clarification}\"";
                    try { await telegram.SendTextMessageAsync(tgConfig, restartMsg); }
                    catch { /* non-critical */ }
                }
            }

            correlation.IsResolved = true;
            await coreDb.SaveChangesAsync();
            return Results.Ok();
        }

        // ── State: waiting → process pipeline control buttons ──
        // callback_data values: "continue", "abort", "restart"
        // Also handle free-text fallback for WhatsApp or non-button responses

        // "abort"
        if (text == "abort" || text.Contains("Abortar"))
        {
            await executor.AbortFromInteractionAsync(correlation.ExecutionId, db, correlation.TenantDbName);
            correlation.IsResolved = true;
            await coreDb.SaveChangesAsync();

            var tgConfig = await GetTgConfigAsync();
            if (tgConfig is not null)
            {
                try { await telegram.SendTextMessageAsync(tgConfig, "❌ Pipeline abortado."); }
                catch { /* non-critical */ }
            }

            return Results.Ok();
        }

        // "restart"
        if (text == "restart" || text.Contains("Reiniciar"))
        {
            correlation.State = "awaiting_restart";
            await coreDb.SaveChangesAsync();

            var tgConfig = await GetTgConfigAsync();
            if (tgConfig is not null)
            {
                try
                {
                    await telegram.SendTextMessageAsync(tgConfig,
                        "🔄 Escribe una aclaracion para reiniciar el pipeline, o envia \"ok\" para reiniciar sin cambios.");
                }
                catch { /* non-critical */ }
            }

            return Results.Ok();
        }

        // "✅ Finalizar" or "▶️ Continuar con: ..." → resume pipeline
        await executor.ResumeFromInteractionAsync(correlation.ExecutionId, text, db, correlation.TenantDbName);
        correlation.IsResolved = true;
        await coreDb.SaveChangesAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error resuming pipeline from Telegram: {ex.Message}");
    }

    return Results.Ok();
});

app.MapHub<ExecutionHub>("/hubs/execution");

app.Run();
