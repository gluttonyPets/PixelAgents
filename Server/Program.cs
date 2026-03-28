using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
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
    // SameSite=None requires Secure (HTTPS). Use Lax for same-origin behind reverse proxy.
    var isProduction = builder.Environment.IsProduction();
    opt.Cookie.SameSite = isProduction ? SameSiteMode.Lax : SameSiteMode.None;
    opt.Cookie.SecurePolicy = isProduction ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
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
builder.Services.AddSingleton<IAiProvider, PexelsProvider>();
builder.Services.AddSingleton<IAiProvider, Json2VideoProvider>();
builder.Services.AddSingleton<IAiProviderRegistry, AiProviderRegistry>();
builder.Services.AddHttpClient<Server.Services.WhatsApp.WhatsAppService>();
builder.Services.AddHttpClient<Server.Services.Telegram.TelegramService>();
builder.Services.AddHttpClient<Server.Services.Instagram.BufferService>();
builder.Services.AddHttpClient<Server.Services.Canva.CanvaService>();
builder.Services.AddScoped<Server.Services.Telegram.TelegramUpdateHandler>();
builder.Services.AddHostedService<Server.Services.Telegram.TelegramPollingService>();
builder.Services.AddHostedService<Server.Services.Scheduler.SchedulerBackgroundService>();
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
                  if (uri.Host == "localhost" || uri.Host == "127.0.0.1")
                      return true;
                  // In production, allow the configured origin (e.g. "http://123.45.67.89")
                  var allowed = builder.Configuration["AllowedOrigin"] ?? "";
                  if (!string.IsNullOrWhiteSpace(allowed))
                      return origin.TrimEnd('/').Equals(allowed.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
                  return false;
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
        // Migration: add BranchId column for branch-aware interaction
        db.Database.ExecuteSqlRaw(@"
            ALTER TABLE ""TelegramCorrelations"" ADD COLUMN IF NOT EXISTS ""BranchId"" varchar(100)");
        db.Database.ExecuteSqlRaw(@"
            ALTER TABLE ""WhatsAppCorrelations"" ADD COLUMN IF NOT EXISTS ""BranchId"" varchar(100)");
        // Migration: add QueuedMessageData column for interaction queue
        db.Database.ExecuteSqlRaw(@"
            ALTER TABLE ""TelegramCorrelations"" ADD COLUMN IF NOT EXISTS ""QueuedMessageData"" text");
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

/// <summary>
/// Resolves the full disk path for an execution file, trying multiple strategies
/// to handle legacy absolute paths, relative paths, and path changes across deployments.
/// Returns null if the file cannot be found on disk.
/// </summary>
static string? ResolveFilePath(string mediaRoot, string workspacePath, string filePath, ILogger logger)
{
    // Strategy 1: workspace is relative — combine with mediaRoot
    if (!Path.IsPathRooted(workspacePath))
    {
        var candidate = Path.Combine(mediaRoot, workspacePath, filePath);
        if (File.Exists(candidate)) return candidate;
        logger.LogWarning("File not found (relative workspace): {Path}", candidate);
    }
    else
    {
        // Strategy 2: workspace is absolute (legacy) — try as-is
        var candidate = Path.Combine(workspacePath, filePath);
        if (File.Exists(candidate)) return candidate;
        logger.LogWarning("File not found (absolute workspace): {Path}", candidate);

        // Strategy 3: extract relative part after "GeneratedMedia" and re-root
        var separator = $"GeneratedMedia{Path.DirectorySeparatorChar}";
        var altSeparator = "GeneratedMedia/"; // handle Linux paths in Windows DB or vice versa
        var idx = workspacePath.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = workspacePath.IndexOf(altSeparator, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = workspacePath.IndexOf("GeneratedMedia\\", StringComparison.OrdinalIgnoreCase);

        if (idx >= 0)
        {
            var relative = workspacePath[(idx + "GeneratedMedia".Length + 1)..];
            var candidate2 = Path.Combine(mediaRoot, relative, filePath);
            if (File.Exists(candidate2))
            {
                logger.LogInformation("File found via re-rooted path: {Path}", candidate2);
                return candidate2;
            }
            logger.LogWarning("File not found (re-rooted): {Path}", candidate2);
        }
    }

    // Strategy 4: try filePath directly under mediaRoot (flat structure fallback)
    var flat = Path.Combine(mediaRoot, filePath);
    if (File.Exists(flat)) return flat;

    logger.LogError("Execution file not found anywhere. WorkspacePath={Workspace}, FilePath={File}, MediaRoot={Root}",
        workspacePath, filePath, mediaRoot);
    return null;
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

    // Inject built-in Checkpoint module if not already present
    if ((providerType is null || providerType == "System") && (moduleType is null || moduleType == "Checkpoint"))
    {
        if (!modules.Any(m => m.ModuleType == "Checkpoint"))
        {
            // Check DB directly in case another request already created it
            var existing = await db.AiModules
                .FirstOrDefaultAsync(m => m.ModuleType == "Checkpoint" && m.ProviderType == "System");

            if (existing is null)
            {
                existing = new AiModule
                {
                    Id = Guid.NewGuid(),
                    Name = "Checkpoint",
                    Description = "Pausa la ejecucion para revisar los datos antes de continuar",
                    ProviderType = "System",
                    ModuleType = "Checkpoint",
                    ModelName = "checkpoint",
                    IsEnabled = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.AiModules.Add(existing);
                try { await db.SaveChangesAsync(); }
                catch
                {
                    // Race condition: another request created it — reload
                    db.ChangeTracker.Clear();
                    existing = await db.AiModules
                        .FirstOrDefaultAsync(m => m.ModuleType == "Checkpoint" && m.ProviderType == "System");
                }
            }

            if (existing is not null)
            {
                modules.Add(new AiModuleResponse(existing.Id, existing.Name, existing.Description,
                    existing.ProviderType, existing.ModuleType, existing.ModelName,
                    null, null, null, true, existing.CreatedAt, existing.UpdatedAt));
            }
        }
    }

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

// ==================== Module File Endpoints ====================

app.MapPost("/api/modules/{moduleId}/files", async (
    Guid moduleId, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var module = await db.AiModules.FindAsync(moduleId);
    if (module is null) return Results.NotFound();

    var form = await ctx.Request.ReadFormAsync();
    var files = form.Files;
    if (files.Count == 0) return Results.BadRequest("No se adjuntaron archivos");

    var claims = await um.GetClaimsAsync((await um.GetUserAsync(ctx.User))!);
    var tenantDbName = claims.First(c => c.Type == "db_name").Value;
    var storageDir = Path.Combine(Directory.GetCurrentDirectory(), "GeneratedMedia", tenantDbName, "module-files", moduleId.ToString());
    Directory.CreateDirectory(storageDir);

    var result = new List<ModuleFileResponse>();

    foreach (var file in files)
    {
        var id = Guid.NewGuid();
        var ext = Path.GetExtension(file.FileName);
        var storedName = $"{id}{ext}";
        var fullPath = Path.Combine(storageDir, storedName);

        await using (var stream = new FileStream(fullPath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var moduleFile = new ModuleFile
        {
            Id = id,
            AiModuleId = moduleId,
            FileName = file.FileName,
            ContentType = file.ContentType,
            FilePath = Path.Combine(tenantDbName, "module-files", moduleId.ToString(), storedName),
            FileSize = file.Length,
            CreatedAt = DateTime.UtcNow
        };

        db.ModuleFiles.Add(moduleFile);
        result.Add(new ModuleFileResponse(moduleFile.Id, moduleFile.AiModuleId, module.Name,
            moduleFile.FileName, moduleFile.ContentType, moduleFile.FileSize, moduleFile.CreatedAt));
    }

    await db.SaveChangesAsync();
    return Results.Ok(result);
}).RequireAuthorization().DisableAntiforgery();

app.MapGet("/api/modules/{moduleId}/files", async (
    Guid moduleId, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var files = await db.ModuleFiles
        .Where(f => f.AiModuleId == moduleId)
        .OrderByDescending(f => f.CreatedAt)
        .Select(f => new ModuleFileResponse(f.Id, f.AiModuleId, f.AiModule.Name,
            f.FileName, f.ContentType, f.FileSize, f.CreatedAt))
        .ToListAsync();

    return Results.Ok(files);
}).RequireAuthorization();

app.MapGet("/api/module-files", async (
    HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var files = await db.ModuleFiles
        .Include(f => f.AiModule)
        .OrderByDescending(f => f.CreatedAt)
        .Select(f => new ModuleFileResponse(f.Id, f.AiModuleId, f.AiModule.Name,
            f.FileName, f.ContentType, f.FileSize, f.CreatedAt))
        .ToListAsync();

    return Results.Ok(files);
}).RequireAuthorization();

app.MapGet("/api/module-files/{fileId}/download", async (
    Guid fileId, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var file = await db.ModuleFiles.FindAsync(fileId);
    if (file is null) return Results.NotFound();

    var fullPath = Path.Combine(Directory.GetCurrentDirectory(), "GeneratedMedia", file.FilePath);
    if (!File.Exists(fullPath)) return Results.NotFound("Archivo no encontrado en disco");

    var bytes = await File.ReadAllBytesAsync(fullPath);
    return Results.File(bytes, file.ContentType, file.FileName);
}).RequireAuthorization();

app.MapDelete("/api/module-files/{fileId}", async (
    Guid fileId, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var file = await db.ModuleFiles.FindAsync(fileId);
    if (file is null) return Results.NotFound();

    var fullPath = Path.Combine(Directory.GetCurrentDirectory(), "GeneratedMedia", file.FilePath);
    if (File.Exists(fullPath)) File.Delete(fullPath);

    db.ModuleFiles.Remove(file);
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
        .Include(p => p.ProjectModules)
            .ThenInclude(pm => pm.OrchestratorOutputs.OrderBy(o => o.SortOrder))
        .FirstOrDefaultAsync(p => p.Id == id);

    if (project is null) return Results.NotFound();

    var modules = project.ProjectModules.Select(pm =>
    {
        // Resolve effective model: inspector override takes precedence over module default
        var effectiveModel = pm.AiModule.ModelName;
        if (!string.IsNullOrEmpty(pm.Configuration))
        {
            try
            {
                using var cfgDoc = System.Text.Json.JsonDocument.Parse(pm.Configuration);
                if (cfgDoc.RootElement.TryGetProperty("modelName", out var mnEl)
                    && mnEl.ValueKind == System.Text.Json.JsonValueKind.String
                    && !string.IsNullOrEmpty(mnEl.GetString()))
                    effectiveModel = mnEl.GetString()!;
            }
            catch { }
        }
        return new ProjectModuleResponse(pm.Id, pm.AiModuleId, pm.AiModule.Name,
            pm.AiModule.ModuleType, effectiveModel, pm.StepOrder, pm.StepName,
            pm.InputMapping, pm.Configuration, pm.IsActive,
            pm.BranchId, pm.BranchFromStep, pm.PosX, pm.PosY,
            pm.AiModule.ModuleType == "Orchestrator"
                ? pm.OrchestratorOutputs.Select(o => new OrchestratorOutputResponse(
                    o.Id, o.OutputKey, o.Label, o.Prompt, o.DataType, o.SortOrder)).ToList()
                : null);
    }).ToList();

    var connections = await db.ModuleConnections
        .Where(c => c.ProjectId == id)
        .Select(c => new ModuleConnectionResponse(c.Id, c.FromModuleId, c.FromPort, c.ToModuleId, c.ToPort))
        .ToListAsync();

    return Results.Ok(new ProjectDetailResponse(
        project.Id, project.Name, project.Description, project.Context,
        project.CreatedAt, project.UpdatedAt, modules, project.GraphLayout, connections));
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

app.MapPut("/api/projects/{id}/graph", async (
    Guid id, GraphLayoutRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects.FindAsync(id);
    if (project is null) return Results.NotFound();

    project.GraphLayout = req.GraphLayout;
    project.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok();
}).RequireAuthorization();

// Save full graph: node positions + connections + recompute StepOrder/InputMapping
app.MapPut("/api/projects/{projectId}/graph/save", async (
    Guid projectId, SaveGraphRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects.FindAsync(projectId);
    if (project is null) return Results.NotFound();

    var modules = await db.ProjectModules
        .Include(x => x.AiModule)
        .Where(x => x.ProjectId == projectId)
        .ToListAsync();

    var now = DateTime.UtcNow;

    // 1. Update node positions
    foreach (var pos in req.Positions)
    {
        var pm = modules.FirstOrDefault(m => m.Id == pos.ModuleId);
        if (pm is null) continue;
        pm.PosX = pos.PosX;
        pm.PosY = pos.PosY;
        pm.UpdatedAt = now;
    }

    // 2. Replace all connections (delete old, insert new)
    var oldConnections = await db.ModuleConnections
        .Where(c => c.ProjectId == projectId)
        .ToListAsync();
    db.ModuleConnections.RemoveRange(oldConnections);

    var moduleIds = modules.Select(m => m.Id).ToHashSet();
    foreach (var conn in req.Connections)
    {
        if (!moduleIds.Contains(conn.FromModuleId) || !moduleIds.Contains(conn.ToModuleId))
            continue;
        db.ModuleConnections.Add(new ModuleConnection
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            FromModuleId = conn.FromModuleId,
            FromPort = conn.FromPort,
            ToModuleId = conn.ToModuleId,
            ToPort = conn.ToPort,
            CreatedAt = now
        });
    }

    // 3. Topological sort: derive StepOrder from connections
    var incomingEdges = moduleIds.ToDictionary(id => id, _ => new List<Guid>());
    foreach (var conn in req.Connections)
    {
        if (moduleIds.Contains(conn.FromModuleId) && moduleIds.Contains(conn.ToModuleId))
            incomingEdges[conn.ToModuleId].Add(conn.FromModuleId);
    }

    var outgoingEdges = moduleIds.ToDictionary(id => id, _ => new List<Guid>());
    foreach (var conn in req.Connections)
    {
        if (moduleIds.Contains(conn.FromModuleId) && moduleIds.Contains(conn.ToModuleId))
            outgoingEdges[conn.FromModuleId].Add(conn.ToModuleId);
    }

    // Kahn's algorithm
    var inDegree = moduleIds.ToDictionary(id => id, id => incomingEdges[id].Count);
    var queue = new Queue<Guid>(moduleIds.Where(id => inDegree[id] == 0));
    var sorted = new List<Guid>();
    while (queue.Count > 0)
    {
        var current = queue.Dequeue();
        sorted.Add(current);
        foreach (var next in outgoingEdges[current])
        {
            inDegree[next]--;
            if (inDegree[next] == 0) queue.Enqueue(next);
        }
    }
    // Append unreachable (cycle or disconnected)
    foreach (var id in moduleIds)
        if (!sorted.Contains(id)) sorted.Add(id);

    // 4. Update StepOrder and InputMapping
    // Temporarily set all to negative to avoid unique constraint violations
    foreach (var pm in modules)
    {
        pm.StepOrder = -(pm.StepOrder + 1000);
    }
    await db.SaveChangesAsync();

    for (int i = 0; i < sorted.Count; i++)
    {
        var pm = modules.FirstOrDefault(m => m.Id == sorted[i]);
        if (pm is null) continue;
        pm.StepOrder = i + 1;
        pm.UpdatedAt = now;

        // Derive InputMapping from connections
        var upstream = incomingEdges[pm.Id];
        if (upstream.Count == 0)
        {
            pm.InputMapping = "{\"source\":\"user\"}";
        }
        else
        {
            // Check if upstream provides file or text
            var upModule = modules.FirstOrDefault(m => m.Id == upstream[0]);
            var conn = req.Connections.FirstOrDefault(c => c.FromModuleId == upstream[0] && c.ToModuleId == pm.Id);
            var field = "text";
            if (conn is not null && upModule is not null)
            {
                // Detect by port name convention
                if (conn.FromPort.Contains("image") || conn.FromPort.Contains("video") ||
                    conn.FromPort.Contains("audio") || conn.FromPort.Contains("file") ||
                    conn.FromPort.Contains("design"))
                    field = "file";
            }

            // Include outputKey when connected from a specific orchestrator output port
            if (conn is not null && upModule?.AiModule?.ModuleType == "Orchestrator"
                && !string.IsNullOrEmpty(conn.FromPort) && conn.FromPort.StartsWith("output_"))
            {
                pm.InputMapping = $"{{\"source\":\"previous\",\"field\":\"{field}\",\"outputKey\":\"{conn.FromPort}\"}}";
            }
            else
            {
                pm.InputMapping = $"{{\"source\":\"previous\",\"field\":\"{field}\"}}";
            }
        }

        // For VideoEdit: detect connections to the input_overlays port and store the source step
        if (pm.AiModule?.ModuleType == "VideoEdit")
        {
            var resourceConn = req.Connections.FirstOrDefault(c => c.ToModuleId == pm.Id && c.ToPort == "input_resources");
            if (resourceConn is not null)
            {
                var resourceModule = modules.FirstOrDefault(m => m.Id == resourceConn.FromModuleId);
                if (resourceModule is not null)
                {
                    var cfgDict = new Dictionary<string, object>();
                    if (!string.IsNullOrEmpty(pm.Configuration))
                    {
                        try { cfgDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(pm.Configuration) ?? new(); }
                        catch { cfgDict = new(); }
                    }
                    cfgDict["resourceSourceStep"] = resourceModule.StepOrder;
                    pm.Configuration = System.Text.Json.JsonSerializer.Serialize(cfgDict);
                }
            }
        }

        // For Coordinator: store all input connections as inputSources map
        if (pm.AiModule?.ModuleType == "Coordinator")
        {
            var coordConns = req.Connections.Where(c => c.ToModuleId == pm.Id && c.ToPort.StartsWith("input_")).ToList();
            if (coordConns.Count > 0)
            {
                var cfgDict = new Dictionary<string, object>();
                if (!string.IsNullOrEmpty(pm.Configuration))
                {
                    try { cfgDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(pm.Configuration) ?? new(); }
                    catch { cfgDict = new(); }
                }
                var inputSources = new Dictionary<string, object>();
                foreach (var conn in coordConns)
                {
                    var srcModule = modules.FirstOrDefault(m => m.Id == conn.FromModuleId);
                    if (srcModule is not null)
                    {
                        inputSources[conn.ToPort] = new Dictionary<string, object>
                        {
                            ["stepOrder"] = srcModule.StepOrder,
                            ["moduleType"] = srcModule.AiModule?.ModuleType ?? "Unknown",
                            ["fromPort"] = conn.FromPort
                        };
                    }
                }
                cfgDict["coordinatorInputs"] = inputSources;
                pm.Configuration = System.Text.Json.JsonSerializer.Serialize(cfgDict);
            }
        }
    }

    // 5. Auto-detect branches from fork points in the connection graph
    // Find fork points: nodes with more than one downstream connection
    var forkPoints = outgoingEdges.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).ToList();

    if (forkPoints.Count > 0)
    {
        // For each fork, determine which downstream path is "main" and which are branches.
        // Walk from each fork's downstream nodes to classify paths.
        var branchAssignment = new Dictionary<Guid, (string BranchId, int? BranchFromStep)>();

        // First mark all as main by default
        foreach (var m in modules)
            branchAssignment[m.Id] = (m.BranchId ?? "main", m.BranchFromStep);

        foreach (var forkId in forkPoints)
        {
            var forkModule = modules.FirstOrDefault(m => m.Id == forkId);
            if (forkModule is null) continue;
            var forkStepOrder = forkModule.StepOrder;

            var downstream = outgoingEdges[forkId];
            if (downstream.Count <= 1) continue;

            // The first downstream node with BranchId == "main" stays main.
            // If none, the one with lowest StepOrder stays main.
            var mainDown = downstream.FirstOrDefault(d =>
            {
                var dm = modules.FirstOrDefault(m => m.Id == d);
                return dm?.BranchId == "main";
            });
            if (mainDown == Guid.Empty)
                mainDown = downstream.OrderBy(d => modules.FirstOrDefault(m => m.Id == d)?.StepOrder ?? int.MaxValue).First();

            int branchLetterIdx = 0;
            foreach (var downId in downstream)
            {
                if (downId == mainDown) continue;

                // This path is a branch — walk all reachable nodes from downId
                var branchNodes = new HashSet<Guid>();
                var walkQueue = new Queue<Guid>();
                walkQueue.Enqueue(downId);
                while (walkQueue.Count > 0)
                {
                    var n = walkQueue.Dequeue();
                    if (!branchNodes.Add(n)) continue;
                    foreach (var next in outgoingEdges.GetValueOrDefault(n, []))
                        if (!downstream.Contains(next) || next == downId) // don't cross into other fork paths
                            walkQueue.Enqueue(next);
                }

                // Use existing branch name if any node already has one, otherwise generate
                var existingBranch = branchNodes
                    .Select(n => modules.FirstOrDefault(m => m.Id == n))
                    .Where(m => m is not null && m.BranchId != "main" && !string.IsNullOrEmpty(m.BranchId))
                    .Select(m => m!.BranchId)
                    .FirstOrDefault();

                var branchName = existingBranch ?? $"branch-{forkStepOrder}-{(char)('a' + branchLetterIdx)}";
                branchLetterIdx++;

                foreach (var nodeId in branchNodes)
                {
                    branchAssignment[nodeId] = (branchName, forkStepOrder);
                }
            }

            // Ensure main downstream path stays main
            var mainWalk = new Queue<Guid>();
            mainWalk.Enqueue(mainDown);
            while (mainWalk.Count > 0)
            {
                var n = mainWalk.Dequeue();
                if (branchAssignment.TryGetValue(n, out var ba) && ba.BranchId == "main")
                {
                    foreach (var next in outgoingEdges.GetValueOrDefault(n, []))
                        mainWalk.Enqueue(next);
                }
            }
        }

        // Apply branch assignments
        foreach (var m in modules)
        {
            if (branchAssignment.TryGetValue(m.Id, out var ba))
            {
                m.BranchId = ba.BranchId;
                m.BranchFromStep = ba.BranchFromStep;
            }
        }
    }
    else
    {
        // No forks — all modules are main
        foreach (var m in modules)
        {
            m.BranchId = "main";
            m.BranchFromStep = null;
        }
    }

    // 6. Persist scene counts into module Configuration
    if (req.SceneCounts is { Count: > 0 })
    {
        foreach (var sc in req.SceneCounts)
        {
            var pm = modules.FirstOrDefault(m => m.Id == sc.ModuleId);
            if (pm is null) continue;

            var configDict = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(pm.Configuration))
            {
                try
                {
                    var existing = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(pm.Configuration);
                    if (existing is not null)
                        foreach (var kv in existing)
                            configDict[kv.Key] = kv.Value;
                }
                catch { /* ignore malformed config */ }
            }

            configDict["sceneCount"] = sc.SceneCount;
            pm.Configuration = System.Text.Json.JsonSerializer.Serialize(configDict);
            pm.UpdatedAt = now;
        }
    }

    // 7. Persist module config entries
    if (req.ModuleConfigs is { Count: > 0 })
    {
        foreach (var mc in req.ModuleConfigs)
        {
            var pm = modules.FirstOrDefault(m => m.Id == mc.ModuleId);
            if (pm is null) continue;

            var configDict = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(pm.Configuration))
            {
                try
                {
                    var existing = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(pm.Configuration);
                    if (existing is not null)
                        foreach (var kv in existing)
                            configDict[kv.Key] = kv.Value;
                }
                catch { /* ignore malformed config */ }
            }

            // Parse value: try bool, then int, then string
            if (bool.TryParse(mc.Value, out var boolVal))
                configDict[mc.Key] = boolVal;
            else if (int.TryParse(mc.Value, out var intVal))
                configDict[mc.Key] = intVal;
            else
                configDict[mc.Key] = mc.Value;

            pm.Configuration = System.Text.Json.JsonSerializer.Serialize(configDict);
            pm.UpdatedAt = now;
        }
    }

    project.UpdatedAt = now;
    await db.SaveChangesAsync();
    return Results.Ok();
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

app.MapPost("/api/projects/{id}/duplicate", async (
    Guid id, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var source = await db.Projects
        .Include(p => p.ProjectModules)
            .ThenInclude(pm => pm.AiModule)
        .Include(p => p.ProjectModules)
            .ThenInclude(pm => pm.OrchestratorOutputs)
        .FirstOrDefaultAsync(p => p.Id == id);

    if (source is null) return Results.NotFound();

    // Load connections from source project
    var sourceConnections = await db.ModuleConnections
        .Where(c => c.ProjectId == id)
        .ToListAsync();

    var newProject = new Project
    {
        Id = Guid.NewGuid(),
        Name = source.Name + " (copia)",
        Description = source.Description,
        Context = source.Context,
        WhatsAppConfig = source.WhatsAppConfig,
        TelegramConfig = source.TelegramConfig,
        InstagramConfig = source.InstagramConfig,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    db.Projects.Add(newProject);

    // Map old module IDs to new module IDs
    var moduleIdMap = new Dictionary<Guid, Guid>();

    foreach (var pm in source.ProjectModules)
    {
        var newId = Guid.NewGuid();
        moduleIdMap[pm.Id] = newId;

        db.ProjectModules.Add(new ProjectModule
        {
            Id = newId,
            ProjectId = newProject.Id,
            AiModuleId = pm.AiModuleId,
            StepOrder = pm.StepOrder,
            StepName = pm.StepName,
            InputMapping = pm.InputMapping,
            Configuration = pm.Configuration,
            IsActive = pm.IsActive,
            BranchId = pm.BranchId,
            BranchFromStep = pm.BranchFromStep,
            PosX = pm.PosX,
            PosY = pm.PosY,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
    }

    // Copy connections, remapping module IDs
    foreach (var conn in sourceConnections)
    {
        if (moduleIdMap.TryGetValue(conn.FromModuleId, out var newFromId)
            && moduleIdMap.TryGetValue(conn.ToModuleId, out var newToId))
        {
            db.ModuleConnections.Add(new ModuleConnection
            {
                Id = Guid.NewGuid(),
                ProjectId = newProject.Id,
                FromModuleId = newFromId,
                FromPort = conn.FromPort,
                ToModuleId = newToId,
                ToPort = conn.ToPort,
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    // Copy orchestrator outputs, remapping module IDs
    foreach (var pm in source.ProjectModules)
    {
        if (pm.OrchestratorOutputs is null) continue;
        foreach (var o in pm.OrchestratorOutputs)
        {
            db.OrchestratorOutputs.Add(new OrchestratorOutput
            {
                Id = Guid.NewGuid(),
                ProjectModuleId = moduleIdMap[pm.Id],
                OutputKey = o.OutputKey,
                Label = o.Label,
                Prompt = o.Prompt,
                DataType = o.DataType,
                SortOrder = o.SortOrder,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
    }

    await db.SaveChangesAsync();

    return Results.Created($"/api/projects/{newProject.Id}",
        new ProjectResponse(newProject.Id, newProject.Name, newProject.Description,
            newProject.Context, newProject.CreatedAt, newProject.UpdatedAt));
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

    // Always assign next available StepOrder to avoid unique constraint violations
    var maxOrder = await db.ProjectModules
        .Where(x => x.ProjectId == projectId && x.BranchId == req.BranchId)
        .MaxAsync(x => (int?)x.StepOrder) ?? -1;
    var stepOrder = maxOrder + 1;

    var pm = new ProjectModule
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        AiModuleId = req.AiModuleId,
        StepOrder = stepOrder,
        BranchId = req.BranchId,
        BranchFromStep = req.BranchFromStep,
        StepName = req.StepName,
        InputMapping = req.InputMapping,
        Configuration = req.Configuration,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    db.ProjectModules.Add(pm);
    await db.SaveChangesAsync();

    // Resolve effective model from configuration override
    var addEffectiveModel = module.ModelName;
    if (!string.IsNullOrEmpty(pm.Configuration))
    {
        try
        {
            using var cfgDoc = System.Text.Json.JsonDocument.Parse(pm.Configuration);
            if (cfgDoc.RootElement.TryGetProperty("modelName", out var mnEl)
                && mnEl.ValueKind == System.Text.Json.JsonValueKind.String
                && !string.IsNullOrEmpty(mnEl.GetString()))
                addEffectiveModel = mnEl.GetString()!;
        }
        catch { }
    }
    return Results.Created($"/api/projects/{projectId}/modules/{pm.Id}",
        new ProjectModuleResponse(pm.Id, pm.AiModuleId, module.Name,
            module.ModuleType, addEffectiveModel, pm.StepOrder, pm.StepName,
            pm.InputMapping, pm.Configuration, pm.IsActive,
            pm.BranchId, pm.BranchFromStep, pm.PosX, pm.PosY));
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
    // Resolve effective model from configuration override
    var updateEffectiveModel = pm.AiModule.ModelName;
    if (!string.IsNullOrEmpty(pm.Configuration))
    {
        try
        {
            using var cfgDoc = System.Text.Json.JsonDocument.Parse(pm.Configuration);
            if (cfgDoc.RootElement.TryGetProperty("modelName", out var mnEl)
                && mnEl.ValueKind == System.Text.Json.JsonValueKind.String
                && !string.IsNullOrEmpty(mnEl.GetString()))
                updateEffectiveModel = mnEl.GetString()!;
        }
        catch { }
    }
    return Results.Ok(new ProjectModuleResponse(pm.Id, pm.AiModuleId, pm.AiModule.Name,
        pm.AiModule.ModuleType, updateEffectiveModel, pm.StepOrder, pm.StepName,
        pm.InputMapping, pm.Configuration, pm.IsActive,
        pm.BranchId, pm.BranchFromStep));
}).RequireAuthorization();

app.MapDelete("/api/projects/{projectId}/branches/{branchId}", async (
    Guid projectId, string branchId, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    if (branchId == "main") return Results.BadRequest(new { error = "No se puede eliminar la rama principal" });

    var branchModules = await db.ProjectModules
        .Where(x => x.ProjectId == projectId && x.BranchId == branchId)
        .ToListAsync();

    if (branchModules.Count == 0) return Results.NotFound();

    db.ProjectModules.RemoveRange(branchModules);
    await db.SaveChangesAsync();
    return Results.Ok();
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

// Batch reorder modules from visual graph topology
app.MapPut("/api/projects/{projectId}/modules/reorder", async (
    Guid projectId, ReorderModulesRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var modules = await db.ProjectModules
        .Where(x => x.ProjectId == projectId)
        .ToListAsync();

    var now = DateTime.UtcNow;
    foreach (var entry in req.Entries)
    {
        var pm = modules.FirstOrDefault(x => x.Id == entry.ModuleId);
        if (pm is null) continue;
        pm.StepOrder = entry.StepOrder;
        pm.InputMapping = entry.InputMapping;
        pm.UpdatedAt = now;
    }

    // Also persist the graph layout in the same transaction
    if (req.GraphLayout is not null)
    {
        var project = await db.Projects.FindAsync(projectId);
        if (project is not null)
        {
            project.GraphLayout = req.GraphLayout;
            project.UpdatedAt = now;
        }
    }

    await db.SaveChangesAsync();
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
    var removedBranch = pm.BranchId;
    db.ProjectModules.Remove(pm);
    await db.SaveChangesAsync();

    // Renumber remaining steps in the SAME branch only
    var remaining = await db.ProjectModules
        .Where(x => x.ProjectId == projectId && x.BranchId == removedBranch && x.StepOrder > removedOrder)
        .OrderBy(x => x.StepOrder)
        .ToListAsync();
    foreach (var r in remaining)
        r.StepOrder--;

    // If we deleted a main-branch step, update BranchFromStep on branches that forked from it or later
    if (removedBranch == "main")
    {
        var branchModules = await db.ProjectModules
            .Where(x => x.ProjectId == projectId && x.BranchFromStep.HasValue)
            .ToListAsync();
        foreach (var bm in branchModules)
        {
            if (bm.BranchFromStep == removedOrder)
            {
                // Branch forked from deleted step — move fork point up one step
                bm.BranchFromStep = removedOrder > 1 ? removedOrder - 1 : 1;
            }
            else if (bm.BranchFromStep > removedOrder)
            {
                // Branch forked from a later step — shift down to match renumbering
                bm.BranchFromStep--;
            }
        }
    }

    if (remaining.Count > 0 || removedBranch == "main")
        await db.SaveChangesAsync();

    return Results.NoContent();
}).RequireAuthorization();

// ==================== Execution Endpoints ====================

app.MapPost("/api/projects/{projectId}/execute", async (
    Guid projectId, ExecuteProjectRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory,
    ExecutionCancellationService cancellation,
    IHubContext<ExecutionHub> hub) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var user = await um.GetUserAsync(ctx.User);
    var claims = await um.GetClaimsAsync(user!);
    var tenantDbName = claims.First(c => c.Type == "db_name").Value;

    var ct = cancellation.Register(projectId);

    // Fire-and-forget: run pipeline in background, return immediately
    _ = Task.Run(async () =>
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var bgFactory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
            await using var bgDb = bgFactory.Create(tenantDbName);
            var executor = scope.ServiceProvider.GetRequiredService<IPipelineExecutor>();

            var execution = await executor.ExecuteAsync(projectId, req.UserInput, bgDb, tenantDbName, ct);

            // Load full result for client
            var exec = await bgDb.ProjectExecutions
                .Include(e => e.StepExecutions)
                    .ThenInclude(s => s.Files)
                .Include(e => e.StepExecutions)
                    .ThenInclude(s => s.ProjectModule)
                        .ThenInclude(pm => pm.AiModule)
                .FirstAsync(e => e.Id == execution.Id);

            var steps = exec.StepExecutions.OrderBy(s => s.StepOrder).Select(s =>
                new StepExecutionResponse(s.Id, s.ProjectModuleId,
                    s.ProjectModule.AiModule.Name, s.StepOrder,
                    s.Status, s.InputData, s.OutputData, s.ErrorMessage,
                    s.CreatedAt, s.CompletedAt, s.EstimatedCost,
                    s.Files.Select(f => new ExecutionFileResponse(
                        f.Id, f.FileName, f.ContentType, f.FilePath,
                        f.Direction, f.FileSize, f.CreatedAt)).ToList()
                )).ToList();

            var detail = new ExecutionDetailResponse(
                exec.Id, exec.ProjectId, exec.Status, exec.WorkspacePath,
                exec.CreatedAt, exec.CompletedAt, exec.UserInput, exec.TotalEstimatedCost, steps);

            // Notify client via SignalR — use specific event based on status
            if (exec.Status == "WaitingForReview")
            {
                await hub.Clients.Group(projectId.ToString())
                    .SendAsync("OrchestratorWaitingForReview", detail);
            }
            else if (exec.Status == "WaitingForCheckpoint")
            {
                await hub.Clients.Group(projectId.ToString())
                    .SendAsync("CheckpointWaitingForReview", detail);
            }
            else
            {
                await hub.Clients.Group(projectId.ToString())
                    .SendAsync("ExecutionCompleted", detail);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Pipeline] Execution failed for project {projectId}: {ex}");

            // Persist failure in DB so it shows in execution history
            try
            {
                using var errScope = app.Services.CreateScope();
                var errFactory = errScope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
                await using var errDb = errFactory.Create(tenantDbName);

                var stuckExec = await errDb.ProjectExecutions
                    .FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Status == "Running");
                if (stuckExec is not null)
                {
                    stuckExec.Status = "Failed";
                    stuckExec.CompletedAt = DateTime.UtcNow;
                    await errDb.SaveChangesAsync();
                }
            }
            catch (Exception dbEx)
            {
                Console.WriteLine($"[Pipeline] Failed to persist error state: {dbEx.Message}");
            }

            await hub.Clients.Group(projectId.ToString())
                .SendAsync("ExecutionFailed", ex.Message);
        }
        finally
        {
            cancellation.Remove(projectId);
        }
    });

    return Results.Accepted(value: new { message = "Ejecucion iniciada" });
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
            e.WorkspacePath, e.CreatedAt, e.CompletedAt, e.UserInput, e.TotalEstimatedCost))
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
        .Include(e => e.StepExecutions)
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
            s.CreatedAt, s.CompletedAt, s.EstimatedCost,
            s.Files.Select(f => new ExecutionFileResponse(
                f.Id, f.FileName, f.ContentType, f.FilePath,
                f.Direction, f.FileSize, f.CreatedAt)).ToList()
        )).ToList();

    return Results.Ok(new ExecutionDetailResponse(
        exec.Id, exec.ProjectId, exec.Status, exec.WorkspacePath,
        exec.CreatedAt, exec.CompletedAt, exec.UserInput, exec.TotalEstimatedCost, steps));
}).RequireAuthorization();

// Get persisted logs for an execution
app.MapGet("/api/executions/{executionId}/logs", async (
    Guid executionId, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var logs = await db.ExecutionLogs
        .Where(l => l.ExecutionId == executionId)
        .OrderBy(l => l.Timestamp)
        .Select(l => new { l.Level, l.Message, l.StepOrder, l.StepName, l.Timestamp })
        .ToListAsync();

    return Results.Ok(logs);
}).RequireAuthorization();

// Retry execution from a specific step
app.MapPost("/api/executions/{executionId}/retry-from-step", async (
    Guid executionId, RetryFromStepRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory,
    ExecutionCancellationService cancellation,
    IHubContext<ExecutionHub> hub) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var user = await um.GetUserAsync(ctx.User);
    var userClaims = await um.GetClaimsAsync(user!);
    var tenantDbName = userClaims.First(c => c.Type == "db_name").Value;

    Guid projectId;
    try
    {
        projectId = await db.ProjectExecutions.Where(e => e.Id == executionId).Select(e => e.ProjectId).FirstAsync();
    }
    catch
    {
        return Results.NotFound(new { error = "Ejecucion no encontrada" });
    }

    var ct = cancellation.Register(projectId);

    _ = Task.Run(async () =>
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var bgFactory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
            await using var bgDb = bgFactory.Create(tenantDbName);
            var executor = scope.ServiceProvider.GetRequiredService<IPipelineExecutor>();

            var execution = await executor.RetryFromStepAsync(executionId, req.StepOrder, req.Comment, bgDb, tenantDbName, ct);

            var exec = await bgDb.ProjectExecutions
                .Include(e => e.StepExecutions)
                    .ThenInclude(s => s.Files)
                .Include(e => e.StepExecutions)
                    .ThenInclude(s => s.ProjectModule)
                        .ThenInclude(pm => pm.AiModule)
                .FirstAsync(e => e.Id == execution.Id);

            var steps = exec.StepExecutions.OrderBy(s => s.StepOrder).Select(s =>
                new StepExecutionResponse(s.Id, s.ProjectModuleId,
                    s.ProjectModule.AiModule.Name, s.StepOrder,
                    s.Status, s.InputData, s.OutputData, s.ErrorMessage,
                    s.CreatedAt, s.CompletedAt, s.EstimatedCost,
                    s.Files.Select(f => new ExecutionFileResponse(
                        f.Id, f.FileName, f.ContentType, f.FilePath,
                        f.Direction, f.FileSize, f.CreatedAt)).ToList()
                )).ToList();

            var detail = new ExecutionDetailResponse(
                exec.Id, exec.ProjectId, exec.Status, exec.WorkspacePath,
                exec.CreatedAt, exec.CompletedAt, exec.UserInput, exec.TotalEstimatedCost, steps);

            if (exec.Status == "WaitingForReview")
            {
                await hub.Clients.Group(projectId.ToString())
                    .SendAsync("OrchestratorWaitingForReview", detail);
            }
            else
            {
                await hub.Clients.Group(projectId.ToString())
                    .SendAsync("ExecutionCompleted", detail);
            }
        }
        catch (Exception ex)
        {
            await hub.Clients.Group(projectId.ToString())
                .SendAsync("ExecutionFailed", ex.Message);
        }
        finally
        {
            cancellation.Remove(projectId);
        }
    });

    return Results.Accepted(value: new { message = "Reintento iniciado" });
}).RequireAuthorization();

// Resume orchestrator after user review
app.MapPost("/api/executions/{executionId}/orchestrator-review", async (
    Guid executionId, OrchestratorReviewRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory,
    ExecutionCancellationService cancellation,
    IHubContext<ExecutionHub> hub) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var user = await um.GetUserAsync(ctx.User);
    var userClaims = await um.GetClaimsAsync(user!);
    var tenantDbName = userClaims.First(c => c.Type == "db_name").Value;

    Guid projectId;
    try
    {
        projectId = await db.ProjectExecutions.Where(e => e.Id == executionId).Select(e => e.ProjectId).FirstAsync();
    }
    catch
    {
        return Results.NotFound(new { error = "Ejecucion no encontrada" });
    }

    var ct = cancellation.Register(projectId);

    _ = Task.Run(async () =>
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var bgFactory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
            await using var bgDb = bgFactory.Create(tenantDbName);
            var executor = scope.ServiceProvider.GetRequiredService<IPipelineExecutor>();

            var execution = await executor.ResumeFromOrchestratorAsync(
                executionId, req.Approved, req.Comment, bgDb, tenantDbName, ct);

            var exec = await bgDb.ProjectExecutions
                .Include(e => e.StepExecutions)
                    .ThenInclude(s => s.Files)
                .Include(e => e.StepExecutions)
                    .ThenInclude(s => s.ProjectModule)
                        .ThenInclude(pm => pm.AiModule)
                .FirstAsync(e => e.Id == execution.Id);

            var steps = exec.StepExecutions.OrderBy(s => s.StepOrder).Select(s =>
                new StepExecutionResponse(s.Id, s.ProjectModuleId,
                    s.ProjectModule.AiModule.Name, s.StepOrder,
                    s.Status, s.InputData, s.OutputData, s.ErrorMessage,
                    s.CreatedAt, s.CompletedAt, s.EstimatedCost,
                    s.Files.Select(f => new ExecutionFileResponse(
                        f.Id, f.FileName, f.ContentType, f.FilePath,
                        f.Direction, f.FileSize, f.CreatedAt)).ToList()
                )).ToList();

            var detail = new ExecutionDetailResponse(
                exec.Id, exec.ProjectId, exec.Status, exec.WorkspacePath,
                exec.CreatedAt, exec.CompletedAt, exec.UserInput, exec.TotalEstimatedCost, steps);

            await hub.Clients.Group(projectId.ToString())
                .SendAsync("ExecutionCompleted", detail);
        }
        catch (Exception ex)
        {
            await hub.Clients.Group(projectId.ToString())
                .SendAsync("ExecutionFailed", ex.Message);
        }
        finally
        {
            cancellation.Remove(projectId);
        }
    });

    return Results.Accepted(value: new { message = req.Approved ? "Plan aprobado, ejecutando tareas..." : "Plan rechazado, feedback guardado" });
}).RequireAuthorization();

// Confirm or abort a paused Checkpoint step
app.MapPost("/api/executions/{executionId}/checkpoint-review", async (
    Guid executionId, CheckpointReviewRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory,
    ExecutionCancellationService cancellation,
    IHubContext<ExecutionHub> hub) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var user = await um.GetUserAsync(ctx.User);
    var userClaims = await um.GetClaimsAsync(user!);
    var tenantDbName = userClaims.First(c => c.Type == "db_name").Value;

    Guid projectId;
    try
    {
        projectId = await db.ProjectExecutions.Where(e => e.Id == executionId).Select(e => e.ProjectId).FirstAsync();
    }
    catch
    {
        return Results.NotFound(new { error = "Ejecucion no encontrada" });
    }

    var ct = cancellation.Register(projectId);

    _ = Task.Run(async () =>
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var bgFactory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
            await using var bgDb = bgFactory.Create(tenantDbName);
            var executor = scope.ServiceProvider.GetRequiredService<IPipelineExecutor>();

            var execution = await executor.ResumeFromCheckpointAsync(executionId, req.Approved, bgDb, tenantDbName, ct);

            var exec = await bgDb.ProjectExecutions
                .Include(e => e.StepExecutions)
                    .ThenInclude(s => s.Files)
                .Include(e => e.StepExecutions)
                    .ThenInclude(s => s.ProjectModule)
                        .ThenInclude(pm => pm.AiModule)
                .FirstAsync(e => e.Id == execution.Id);

            var steps = exec.StepExecutions.OrderBy(s => s.StepOrder).Select(s =>
                new StepExecutionResponse(s.Id, s.ProjectModuleId,
                    s.ProjectModule.AiModule.Name, s.StepOrder,
                    s.Status, s.InputData, s.OutputData, s.ErrorMessage,
                    s.CreatedAt, s.CompletedAt, s.EstimatedCost,
                    s.Files.Select(f => new ExecutionFileResponse(
                        f.Id, f.FileName, f.ContentType, f.FilePath,
                        f.Direction, f.FileSize, f.CreatedAt)).ToList()
                )).ToList();

            var detail = new ExecutionDetailResponse(
                exec.Id, exec.ProjectId, exec.Status, exec.WorkspacePath,
                exec.CreatedAt, exec.CompletedAt, exec.UserInput, exec.TotalEstimatedCost, steps);

            if (exec.Status == "WaitingForCheckpoint")
            {
                await hub.Clients.Group(projectId.ToString())
                    .SendAsync("CheckpointWaitingForReview", detail);
            }
            else
            {
                await hub.Clients.Group(projectId.ToString())
                    .SendAsync("ExecutionCompleted", detail);
            }
        }
        catch (Exception ex)
        {
            await hub.Clients.Group(projectId.ToString())
                .SendAsync("ExecutionFailed", ex.Message);
        }
        finally
        {
            cancellation.Remove(projectId);
        }
    });

    return Results.Accepted(value: new { message = req.Approved ? "Checkpoint aprobado, continuando..." : "Checkpoint abortado" });
}).RequireAuthorization();

// ── OrchestratorOutput CRUD ──
app.MapGet("/api/projects/{projectId}/modules/{moduleId}/orchestrator-outputs", async (
    Guid projectId, Guid moduleId, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var outputs = await db.OrchestratorOutputs
        .Where(o => o.ProjectModuleId == moduleId)
        .OrderBy(o => o.SortOrder)
        .Select(o => new OrchestratorOutputResponse(
            o.Id, o.OutputKey, o.Label, o.Prompt, o.DataType, o.SortOrder))
        .ToListAsync();

    return Results.Ok(outputs);
}).RequireAuthorization();

app.MapPost("/api/projects/{projectId}/modules/{moduleId}/orchestrator-outputs", async (
    Guid projectId, Guid moduleId, OrchestratorOutputRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var pm = await db.ProjectModules.Include(p => p.AiModule)
        .FirstOrDefaultAsync(p => p.Id == moduleId && p.ProjectId == projectId);
    if (pm is null) return Results.NotFound();
    if (pm.AiModule.ModuleType != "Orchestrator")
        return Results.BadRequest(new { error = "El modulo no es de tipo Orchestrator" });

    var existingCount = await db.OrchestratorOutputs.CountAsync(o => o.ProjectModuleId == moduleId);
    var outputKey = $"output_{existingCount + 1}";

    var output = new OrchestratorOutput
    {
        Id = Guid.NewGuid(),
        ProjectModuleId = moduleId,
        OutputKey = outputKey,
        Label = req.Label,
        Prompt = req.Prompt,
        DataType = req.DataType,
        SortOrder = req.SortOrder,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    db.OrchestratorOutputs.Add(output);
    await db.SaveChangesAsync();

    return Results.Created($"/api/projects/{projectId}/modules/{moduleId}/orchestrator-outputs/{output.Id}",
        new OrchestratorOutputResponse(output.Id, output.OutputKey, output.Label, output.Prompt,
            output.DataType, output.SortOrder));
}).RequireAuthorization();

app.MapPut("/api/projects/{projectId}/modules/{moduleId}/orchestrator-outputs/{outputId}", async (
    Guid projectId, Guid moduleId, Guid outputId, OrchestratorOutputRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var output = await db.OrchestratorOutputs
        .FirstOrDefaultAsync(o => o.Id == outputId && o.ProjectModuleId == moduleId);
    if (output is null) return Results.NotFound();

    output.Label = req.Label;
    output.Prompt = req.Prompt;
    output.DataType = req.DataType;
    output.SortOrder = req.SortOrder;
    output.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();

    return Results.Ok(new OrchestratorOutputResponse(output.Id, output.OutputKey, output.Label, output.Prompt,
        output.DataType, output.SortOrder));
}).RequireAuthorization();

app.MapDelete("/api/projects/{projectId}/modules/{moduleId}/orchestrator-outputs/{outputId}", async (
    Guid projectId, Guid moduleId, Guid outputId, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var output = await db.OrchestratorOutputs
        .FirstOrDefaultAsync(o => o.Id == outputId && o.ProjectModuleId == moduleId);
    if (output is null) return Results.NotFound();

    db.OrchestratorOutputs.Remove(output);
    await db.SaveChangesAsync();
    return Results.NoContent();
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
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory,
    IWebHostEnvironment env, ILogger<Program> logger) =>
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

    var mediaRoot = Path.Combine(env.ContentRootPath, "GeneratedMedia");
    var fullPath = ResolveFilePath(mediaRoot, exec.WorkspacePath, file.FilePath, logger);
    if (fullPath is null) return Results.NotFound("Archivo no encontrado en disco");

    var bytes = await File.ReadAllBytesAsync(fullPath);
    return Results.File(bytes, file.ContentType, file.FileName);
}).RequireAuthorization();

// Public file endpoint for external services (e.g., Buffer) that cannot authenticate
app.MapGet("/api/public/files/{tenant}/{executionId}/{fileId}/{fileName}", async (
    string tenant, Guid executionId, Guid fileId, string fileName,
    ITenantDbContextFactory factory, IWebHostEnvironment env, ILogger<Program> logger) =>
{
    UserDbContext db;
    try { db = factory.Create(tenant); }
    catch { return Results.NotFound(); }

    await using (db)
    {
        var exec = await db.ProjectExecutions
            .FirstOrDefaultAsync(e => e.Id == executionId);
        if (exec is null) return Results.NotFound();

        var file = await db.ExecutionFiles
            .FirstOrDefaultAsync(f => f.Id == fileId &&
                f.StepExecution.ExecutionId == executionId);
        if (file is null) return Results.NotFound();

        var mediaRoot = Path.Combine(env.ContentRootPath, "GeneratedMedia");
        var fullPath = ResolveFilePath(mediaRoot, exec.WorkspacePath, file.FilePath, logger);
        if (fullPath is null) return Results.NotFound();

        var bytes = await File.ReadAllBytesAsync(fullPath);
        return Results.File(bytes, file.ContentType, file.FileName);
    }
});

// ==================== Schedule Endpoints ====================

app.MapGet("/api/projects/{projectId:guid}/schedule", async (
    Guid projectId, HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var schedule = await db.ProjectSchedules
        .FirstOrDefaultAsync(s => s.ProjectId == projectId);

    if (schedule is null) return Results.Ok((ScheduleResponse?)null);

    return Results.Ok(new ScheduleResponse(
        schedule.Id, schedule.ProjectId, schedule.IsEnabled,
        schedule.CronExpression, schedule.TimeZone, schedule.UserInput,
        schedule.LastRunAt, schedule.NextRunAt,
        schedule.CreatedAt, schedule.UpdatedAt));
}).RequireAuthorization();

app.MapPost("/api/projects/{projectId:guid}/schedule", async (
    Guid projectId, CreateScheduleRequest req,
    HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects.FindAsync(projectId);
    if (project is null) return Results.NotFound();

    // Only one schedule per project
    var existing = await db.ProjectSchedules.FirstOrDefaultAsync(s => s.ProjectId == projectId);
    if (existing is not null)
        return Results.BadRequest(new { error = "Este proyecto ya tiene una programacion. Usa PUT para actualizarla." });

    var now = DateTime.UtcNow;
    var nextRun = Server.Services.Scheduler.SchedulerBackgroundService.ComputeNextRun(
        req.CronExpression, req.TimeZone, now);

    var schedule = new ProjectSchedule
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        IsEnabled = true,
        CronExpression = req.CronExpression,
        TimeZone = req.TimeZone,
        UserInput = req.UserInput,
        NextRunAt = nextRun,
        CreatedAt = now,
        UpdatedAt = now
    };

    db.ProjectSchedules.Add(schedule);
    await db.SaveChangesAsync();

    return Results.Ok(new ScheduleResponse(
        schedule.Id, schedule.ProjectId, schedule.IsEnabled,
        schedule.CronExpression, schedule.TimeZone, schedule.UserInput,
        schedule.LastRunAt, schedule.NextRunAt,
        schedule.CreatedAt, schedule.UpdatedAt));
}).RequireAuthorization();

app.MapPut("/api/projects/{projectId:guid}/schedule", async (
    Guid projectId, UpdateScheduleRequest req,
    HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var schedule = await db.ProjectSchedules.FirstOrDefaultAsync(s => s.ProjectId == projectId);
    if (schedule is null) return Results.NotFound();

    var now = DateTime.UtcNow;

    schedule.CronExpression = req.CronExpression;
    schedule.TimeZone = req.TimeZone;
    schedule.UserInput = req.UserInput;
    schedule.IsEnabled = req.IsEnabled;
    schedule.NextRunAt = req.IsEnabled
        ? Server.Services.Scheduler.SchedulerBackgroundService.ComputeNextRun(req.CronExpression, req.TimeZone, now)
        : null;
    schedule.UpdatedAt = now;

    await db.SaveChangesAsync();

    return Results.Ok(new ScheduleResponse(
        schedule.Id, schedule.ProjectId, schedule.IsEnabled,
        schedule.CronExpression, schedule.TimeZone, schedule.UserInput,
        schedule.LastRunAt, schedule.NextRunAt,
        schedule.CreatedAt, schedule.UpdatedAt));
}).RequireAuthorization();

app.MapDelete("/api/projects/{projectId:guid}/schedule", async (
    Guid projectId, HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var schedule = await db.ProjectSchedules.FirstOrDefaultAsync(s => s.ProjectId == projectId);
    if (schedule is null) return Results.NotFound();

    db.ProjectSchedules.Remove(schedule);
    await db.SaveChangesAsync();

    return Results.Ok(new { message = "Programacion eliminada" });
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
        if (!string.IsNullOrWhiteSpace(correlation.BranchId))
            await executor.ResumeFromBranchInteractionAsync(
                correlation.ExecutionId, correlation.BranchId, text, db, correlation.TenantDbName);
        else
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
            var baseUrl = builder.Configuration["Telegram:WebhookBaseUrl"]
                ?? builder.Configuration["BaseUrl"]
                ?? "";

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                // No public URL → delete webhook so polling mode works
                Console.WriteLine("[Telegram] No WebhookBaseUrl configured. Deleting webhook for polling mode.");
                await telegram.DeleteWebhookAsync(dto.BotToken);
                return Results.Ok(new { message = "Configuracion Telegram guardada. Modo polling activo (sin webhook).", mode = "polling" });
            }

            var webhookUrl = $"{baseUrl.TrimEnd('/')}/api/webhooks/telegram";
            Console.WriteLine($"[Telegram] Registering webhook: {webhookUrl}");
            await telegram.SetWebhookAsync(dto.BotToken, webhookUrl);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not set Telegram webhook: {ex.Message}");
            return Results.Ok(new { message = "Configuracion guardada, pero no se pudo registrar el webhook de Telegram.", webhookError = ex.Message });
        }
    }

    return Results.Ok(new { message = "Configuracion Telegram guardada y webhook registrado.", mode = "webhook" });
}).RequireAuthorization();

// Telegram webhook diagnostic endpoint
app.MapGet("/api/projects/{projectId:guid}/telegram-webhook-info", async (
    Guid projectId, HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory,
    Server.Services.Telegram.TelegramService telegram) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects.FindAsync(projectId);
    if (project is null) return Results.NotFound();

    if (string.IsNullOrWhiteSpace(project.TelegramConfig))
        return Results.Ok(new { error = "No hay configuracion de Telegram" });

    var config = System.Text.Json.JsonSerializer.Deserialize<TelegramConfigDto>(project.TelegramConfig);
    if (config is null || string.IsNullOrWhiteSpace(config.BotToken))
        return Results.Ok(new { error = "BotToken vacio" });

    try
    {
        var info = await telegram.GetWebhookInfoAsync(config.BotToken);
        return Results.Ok(new { webhookInfo = info });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { error = ex.Message });
    }
}).RequireAuthorization();

// ==================== Instagram (Buffer) Config Endpoints ====================

app.MapGet("/api/projects/{projectId:guid}/instagram-config", async (
    Guid projectId, HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects.FindAsync(projectId);
    if (project is null) return Results.NotFound();

    if (string.IsNullOrWhiteSpace(project.InstagramConfig))
        return Results.Ok(new BufferConfigDto("", ""));

    var config = System.Text.Json.JsonSerializer.Deserialize<BufferConfigDto>(project.InstagramConfig);
    return Results.Ok(config);
}).RequireAuthorization();

app.MapPut("/api/projects/{projectId:guid}/instagram-config", async (
    Guid projectId, BufferConfigDto dto, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects.FindAsync(projectId);
    if (project is null) return Results.NotFound();

    project.InstagramConfig = System.Text.Json.JsonSerializer.Serialize(dto);
    project.UpdatedAt = DateTime.UtcNow;

    // Ensure the Buffer Publish sentinel module exists
    var hasBufferPublish = await db.AiModules.AnyAsync(m => m.ModuleType == "Publish" && m.ModelName == "instagram");
    if (!hasBufferPublish)
    {
        db.AiModules.Add(new AiModule
        {
            Id = Guid.NewGuid(),
            Name = "Buffer Publish",
            Description = "Publica contenido en redes sociales (Instagram, Twitter, etc.) via Buffer.",
            ProviderType = "System",
            ModuleType = "Publish",
            ModelName = "instagram",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Configuracion Buffer guardada" });
}).RequireAuthorization();

// ==================== Telegram Webhook Endpoint ====================

app.MapPost("/api/webhooks/telegram", async (
    HttpContext ctx, Server.Services.Telegram.TelegramUpdateHandler handler) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    System.Text.Json.JsonElement json;
    try { json = System.Text.Json.JsonDocument.Parse(body).RootElement; }
    catch { return Results.Ok(); }

    await handler.ProcessUpdateAsync(json);
    return Results.Ok();
});

app.MapHub<ExecutionHub>("/hubs/execution");

// ── Build info ──
app.MapGet("/api/build-info", () =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "build-info.json");
    if (File.Exists(path))
    {
        var json = File.ReadAllText(path);
        return Results.Content(json, "application/json");
    }
    return Results.Ok(new { commitHash = "unknown", buildDate = "unknown" });
});

app.Run();
