using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Server;
using Server.Data;
using Server.Models;
using Server.Services;
using Server.Services.Ai;
using Server.Services.Ai.Handlers;
using Server.Hubs;

var builder = WebApplication.CreateBuilder(args);
const long MaxModuleUploadSize = 512L * 1024 * 1024;

builder.WebHost.ConfigureKestrel(opt =>
{
    opt.Limits.MaxRequestBodySize = MaxModuleUploadSize;
});

builder.Services.Configure<FormOptions>(opt =>
{
    opt.MultipartBodyLengthLimit = MaxModuleUploadSize;
});

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
builder.Services.AddSingleton<IAiProvider, GrokProvider>();
builder.Services.AddSingleton<IAiProviderRegistry, AiProviderRegistry>();
builder.Services.AddTransient<IModuleHandler, StartModuleHandler>();
builder.Services.AddTransient<IModuleHandler, StaticTextModuleHandler>();
builder.Services.AddTransient<IModuleHandler, FileUploadModuleHandler>();
builder.Services.AddTransient<IModuleHandler, TextModuleHandler>();
builder.Services.AddTransient<IModuleHandler, ImageModuleHandler>();
builder.Services.AddTransient<IModuleHandler, AudioModuleHandler>();
builder.Services.AddTransient<IModuleHandler, TranscriptionModuleHandler>();
builder.Services.AddTransient<IModuleHandler, EmbeddingsModuleHandler>();
builder.Services.AddTransient<IModuleHandler, OrchestratorModuleHandler>();
builder.Services.AddTransient<IModuleHandler, SceneModuleHandler>();
builder.Services.AddTransient<IModuleHandler, CheckpointModuleHandler>();
builder.Services.AddTransient<IModuleHandler, InteractionModuleHandler>();
builder.Services.AddTransient<IModuleHandler, DesignModuleHandler>();
builder.Services.AddTransient<IModuleHandler, PublishModuleHandler>();
builder.Services.AddTransient<IModuleHandler, ShopifyBlogModuleHandler>();
builder.Services.AddHttpClient<Server.Services.Shopify.ShopifyService>();
builder.Services.AddHttpClient<Server.Services.Telegram.TelegramService>();
builder.Services.AddHttpClient<Server.Services.Instagram.BufferService>();
builder.Services.AddSingleton<Server.Services.Instagram.BufferImagePoolService>();
builder.Services.AddHttpClient<Server.Services.Canva.CanvaService>();
builder.Services.AddScoped<Server.Services.Telegram.TelegramUpdateHandler>();
builder.Services.AddHostedService<Server.Services.Telegram.TelegramPollingService>();
builder.Services.AddHostedService<Server.Services.Scheduler.SchedulerBackgroundService>();
builder.Services.AddTransient<IPipelineExecutor, GraphPipelineExecutor>();
builder.Services.AddScoped<IPromptPlannerService, PromptPlannerService>();
builder.Services.AddScoped<IPromptBuilderService, PromptBuilderService>();
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
            CREATE TABLE IF NOT EXISTS ""TelegramCorrelations"" (
                ""Id"" uuid NOT NULL PRIMARY KEY,
                ""ExecutionId"" uuid NOT NULL,
                ""ProjectModuleId"" uuid NOT NULL,
                ""TenantDbName"" varchar(200) NOT NULL,
                ""ChatId"" varchar(50) NOT NULL,
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
        // Migration: graph interactions are correlated by module id, never by step order or branch.
        db.Database.ExecuteSqlRaw(@"
            ALTER TABLE ""TelegramCorrelations"" ADD COLUMN IF NOT EXISTS ""ProjectModuleId"" uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'");
        db.Database.ExecuteSqlRaw(@"ALTER TABLE ""TelegramCorrelations"" DROP COLUMN IF EXISTS ""StepOrder""");
        db.Database.ExecuteSqlRaw(@"ALTER TABLE ""TelegramCorrelations"" DROP COLUMN IF EXISTS ""BranchId""");
        // Migration: add QueuedMessageData column for interaction queue
        db.Database.ExecuteSqlRaw(@"
            ALTER TABLE ""TelegramCorrelations"" ADD COLUMN IF NOT EXISTS ""QueuedMessageData"" text");
        // Migration: add EditStateData column for the out-of-band Edit flow
        db.Database.ExecuteSqlRaw(@"
            ALTER TABLE ""TelegramCorrelations"" ADD COLUMN IF NOT EXISTS ""EditStateData"" text");
        // Migration: add ProjectId column for correlations not tied to an execution (planning flow)
        db.Database.ExecuteSqlRaw(@"
            ALTER TABLE ""TelegramCorrelations"" ADD COLUMN IF NOT EXISTS ""ProjectId"" uuid");
        // Idempotencia de updates de Telegram: guard compartido para no procesar dos veces
        // el mismo update (reintento de webhook, reproceso de polling o instancias solapadas).
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""ProcessedTelegramUpdates"" (
                ""UpdateKey"" text NOT NULL PRIMARY KEY,
                ""ProcessedAt"" timestamp with time zone NOT NULL
            )");
        db.Database.ExecuteSqlRaw(@"
            CREATE INDEX IF NOT EXISTS ""IX_ProcessedTelegramUpdates_ProcessedAt""
            ON ""ProcessedTelegramUpdates"" (""ProcessedAt"")");
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
/// Registra en el historial las versiones de los prompts (systemPrompt / imagePrompt)
/// de un modulo cuando cambian respecto a su configuracion anterior. La primera vez
/// que se registra un campo, guarda tambien el valor previo como "baseline" para
/// poder restaurarlo. No llama a SaveChanges: las entidades se persisten con el guardado
/// del modulo.
/// </summary>
static async Task RecordPromptVersionsAsync(
    UserDbContext db, Guid moduleId, string? oldConfig, string? newConfig, CancellationToken ct)
{
    foreach (var field in new[] { "systemPrompt", "imagePrompt" })
    {
        var oldVal = ExtractConfigString(oldConfig, field) ?? "";
        var newVal = ExtractConfigString(newConfig, field) ?? "";
        if (oldVal == newVal) continue; // sin cambios en este campo

        var now = DateTime.UtcNow;

        var hasHistory = await db.PromptVersions
            .AnyAsync(v => v.AiModuleId == moduleId && v.Field == field, ct);

        // Semilla del valor previo la primera vez que se registra historial de este campo.
        if (!hasHistory && !string.IsNullOrWhiteSpace(oldVal))
        {
            db.PromptVersions.Add(new PromptVersion
            {
                Id = Guid.NewGuid(),
                AiModuleId = moduleId,
                Field = field,
                Content = oldVal,
                Source = "baseline",
                CreatedAt = now.AddSeconds(-1),
            });
        }

        if (!string.IsNullOrWhiteSpace(newVal))
        {
            db.PromptVersions.Add(new PromptVersion
            {
                Id = Guid.NewGuid(),
                AiModuleId = moduleId,
                Field = field,
                Content = newVal,
                Source = "edit",
                CreatedAt = now,
            });
        }
    }
}

static string? ExtractConfigString(string? config, string prop)
{
    if (string.IsNullOrWhiteSpace(config)) return null;
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(config);
        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
            doc.RootElement.TryGetProperty(prop, out var v) &&
            v.ValueKind == System.Text.Json.JsonValueKind.String)
            return v.GetString();
    }
    catch { }
    return null;
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

/// <summary>
/// Copies the configuration from an AiModule but excludes the systemPrompt field.
/// This ensures that systemPrompt is always read from the current AiModule configuration
/// rather than being frozen at the time the module is added to a pipeline.
/// Other fields (like numberOfImages, temperature, modelName) are copied to allow
/// per-pipeline customization.
/// </summary>
static string? CopyConfigurationExcludingSystemPrompt(string? sourceConfig)
{
    if (string.IsNullOrWhiteSpace(sourceConfig)) return null;
    
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(sourceConfig);
        var root = doc.RootElement;
        
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            return sourceConfig;
        
        var filtered = new Dictionary<string, System.Text.Json.JsonElement>();
        foreach (var prop in root.EnumerateObject())
        {
            // Exclude systemPrompt - it should always come from AiModule
            if (!prop.Name.Equals("systemPrompt", StringComparison.OrdinalIgnoreCase))
            {
                filtered[prop.Name] = prop.Value.Clone();
            }
        }
        
        if (filtered.Count == 0) return null;
        
        return System.Text.Json.JsonSerializer.Serialize(filtered);
    }
    catch
    {
        // If parsing fails, return original config
        return sourceConfig;
    }
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
        new ApiKeyResponse(apiKey.Id, apiKey.Name, apiKey.ProviderType, apiKey.CreatedAt, apiKey.UpdatedAt, 0));
}).RequireAuthorization();

app.MapGet("/api/apikeys", async (
    HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var keys = await db.ApiKeys
        .OrderByDescending(k => k.CreatedAt)
        .Select(k => new ApiKeyResponse(
            k.Id, k.Name, k.ProviderType, k.CreatedAt, k.UpdatedAt,
            k.AiModules.Count))
        .ToListAsync();

    return Results.Ok(keys);
}).RequireAuthorization();

app.MapGet("/api/apikeys/{id}", async (
    Guid id, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var key = await db.ApiKeys
        .Where(k => k.Id == id)
        .Select(k => new ApiKeyResponse(
            k.Id, k.Name, k.ProviderType, k.CreatedAt, k.UpdatedAt,
            k.AiModules.Count))
        .FirstOrDefaultAsync();
    if (key is null) return Results.NotFound();

    return Results.Ok(key);
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
    var modulesCount = await db.AiModules.CountAsync(m => m.ApiKeyId == key.Id);
    return Results.Ok(new ApiKeyResponse(key.Id, key.Name, key.ProviderType, key.CreatedAt, key.UpdatedAt, modulesCount));
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

// ==================== Rule Endpoints ====================

app.MapGet("/api/rules", async (
    HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var rules = await db.Rules
        .OrderBy(r => r.SortOrder).ThenBy(r => r.CreatedAt)
        .Select(r => new RuleResponse(r.Id, r.Title, r.Content, r.IsActive, r.SortOrder, r.CreatedAt, r.UpdatedAt))
        .ToListAsync();

    return Results.Ok(rules);
}).RequireAuthorization();

app.MapPost("/api/rules", async (
    CreateRuleRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Content))
        return Results.BadRequest("Title y Content son obligatorios");

    var rule = new Rule
    {
        Id = Guid.NewGuid(),
        Title = req.Title,
        Content = req.Content,
        IsActive = req.IsActive,
        SortOrder = req.SortOrder,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
    db.Rules.Add(rule);
    await db.SaveChangesAsync();

    return Results.Created($"/api/rules/{rule.Id}",
        new RuleResponse(rule.Id, rule.Title, rule.Content, rule.IsActive, rule.SortOrder, rule.CreatedAt, rule.UpdatedAt));
}).RequireAuthorization();

app.MapPut("/api/rules/{id}", async (
    Guid id, UpdateRuleRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var rule = await db.Rules.FindAsync(id);
    if (rule is null) return Results.NotFound();

    if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Content))
        return Results.BadRequest("Title y Content son obligatorios");

    rule.Title = req.Title;
    rule.Content = req.Content;
    rule.IsActive = req.IsActive;
    rule.SortOrder = req.SortOrder;
    rule.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(new RuleResponse(rule.Id, rule.Title, rule.Content, rule.IsActive, rule.SortOrder, rule.CreatedAt, rule.UpdatedAt));
}).RequireAuthorization();

app.MapDelete("/api/rules/{id}", async (
    Guid id, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var rule = await db.Rules.FindAsync(id);
    if (rule is null) return Results.NotFound();

    db.Rules.Remove(rule);
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

    if (SystemModuleCatalog.TryGetDefinition(req.ProviderType, req.ModuleType, out var systemDefinition))
    {
        var builtIn = await SystemModuleCatalog.EnsureModuleAsync(db, systemDefinition);
        return Results.Ok(new AiModuleResponse(builtIn.Id, builtIn.Name, builtIn.Description,
            builtIn.ProviderType, builtIn.ModuleType, builtIn.ModelName,
            builtIn.ApiKeyId, null, builtIn.Configuration, builtIn.IsEnabled,
            builtIn.CreatedAt, builtIn.UpdatedAt));
    }
    if (string.Equals(req.ProviderType, "System", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Los modulos de recursos solo pueden ser Archivos o Texto plano." });

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

    await SystemModuleCatalog.EnsureDefaultModulesAsync(db);

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

    modules = modules
        .Where(m => !(m.ProviderType == "System" && m.ModuleType == "Scene"))
        .GroupBy(m => m.ProviderType == "System" && m.ModuleType is "FileUpload" or "StaticText"
            ? $"System:{m.ModuleType}"
            : m.Id.ToString())
        .Select(g => g.OrderBy(m => m.CreatedAt).First())
        .ToList();

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

// Devuelve en cuantos proyectos se usa un modulo de catalogo. Permite excluir el
// proyecto actual para advertir al usuario antes de editar un modulo compartido.
app.MapGet("/api/modules/{id}/usage", async (
    Guid id, Guid? excludeProjectId, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var query = db.ProjectModules
        .Include(pm => pm.Project)
        .Where(pm => pm.AiModuleId == id);
    if (excludeProjectId is not null)
        query = query.Where(pm => pm.ProjectId != excludeProjectId);

    var projects = await query
        .Select(pm => new { pm.ProjectId, pm.Project.Name })
        .Distinct()
        .ToListAsync();

    return Results.Ok(new ModuleUsageResponse(projects.Count, projects.Select(p => p.Name).ToList()));
}).RequireAuthorization();

app.MapPut("/api/modules/{id}", async (
    Guid id, UpdateAiModuleRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory, CancellationToken ct) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var m = await db.AiModules.FindAsync(new object?[] { id }, ct);
    if (m is null) return Results.NotFound();

    // Registrar historial de versiones del prompt si cambia (antes de sobreescribir).
    await RecordPromptVersionsAsync(db, m.Id, m.Configuration, req.Configuration, ct);

    m.Name = req.Name;
    m.Description = req.Description;
    m.ProviderType = req.ProviderType;
    m.ModuleType = req.ModuleType;
    m.ModelName = req.ModelName;
    m.ApiKeyId = req.ApiKeyId;
    m.Configuration = req.Configuration;
    m.IsEnabled = req.IsEnabled;
    m.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync(ct);
    return Results.Ok(new AiModuleResponse(m.Id, m.Name, m.Description,
        m.ProviderType, m.ModuleType, m.ModelName,
        m.ApiKeyId, null, m.Configuration, m.IsEnabled,
        m.CreatedAt, m.UpdatedAt));
}).RequireAuthorization();

app.MapGet("/api/modules/{id}/prompt-history", async (
    Guid id, string? field, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory, CancellationToken ct) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var m = await db.AiModules.FindAsync(new object?[] { id }, ct);
    if (m is null) return Results.NotFound();

    var q = db.PromptVersions.Where(v => v.AiModuleId == id);
    if (!string.IsNullOrWhiteSpace(field))
        q = q.Where(v => v.Field == field);

    var items = await q
        .OrderByDescending(v => v.CreatedAt)
        .Take(200)
        .Select(v => new PromptVersionResponse(v.Id, v.Field, v.Content, v.Source, v.CreatedAt))
        .ToListAsync(ct);

    return Results.Ok(items);
}).RequireAuthorization();

app.MapDelete("/api/modules/{id}", async (
    Guid id, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var m = await db.AiModules.FindAsync(id);
    if (m is null) return Results.NotFound();
    if (SystemModuleCatalog.TryGetDefinition(m.ProviderType, m.ModuleType, out _))
        return Results.BadRequest(new { error = "Los modulos de sistema son integrados y no se pueden eliminar." });

    db.AiModules.Remove(m);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

// ==================== Module File Endpoints ====================

app.MapPost("/api/project-modules/{projectModuleId}/files", async (
    Guid projectModuleId, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var pm = await db.ProjectModules
        .Include(p => p.AiModule)
        .Include(p => p.Project)
        .FirstOrDefaultAsync(p => p.Id == projectModuleId);
    if (pm is null) return Results.NotFound();

    var form = await ctx.Request.ReadFormAsync();
    var files = form.Files;
    if (files.Count == 0) return Results.BadRequest("No se adjuntaron archivos");
    foreach (var file in files)
    {
        if (file.Length > MaxModuleUploadSize)
            return Results.BadRequest(new { error = $"El archivo '{file.FileName}' supera el limite de 512 MB." });
    }

    var claims = await um.GetClaimsAsync((await um.GetUserAsync(ctx.User))!);
    var tenantDbName = claims.First(c => c.Type == "db_name").Value;
    var storageDir = Path.Combine(Directory.GetCurrentDirectory(), "GeneratedMedia", tenantDbName, "module-files", projectModuleId.ToString());
    Directory.CreateDirectory(storageDir);

    var result = new List<ModuleFileResponse>();

    foreach (var file in files)
    {
        var id = Guid.NewGuid();
        var originalName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(originalName))
            originalName = $"{id}.bin";
        var contentType = string.IsNullOrWhiteSpace(file.ContentType)
            ? "application/octet-stream"
            : file.ContentType;
        if (contentType.Length > 100)
            contentType = contentType[..100];
        var ext = Path.GetExtension(originalName);
        var storedName = $"{id}{ext}";
        var fullPath = Path.Combine(storageDir, storedName);

        await using (var stream = new FileStream(fullPath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var moduleFile = new ModuleFile
        {
            Id = id,
            ProjectModuleId = projectModuleId,
            FileName = originalName,
            ContentType = contentType,
            FilePath = Path.Combine(tenantDbName, "module-files", projectModuleId.ToString(), storedName),
            FileSize = file.Length,
            CreatedAt = DateTime.UtcNow
        };

        db.ModuleFiles.Add(moduleFile);
        result.Add(new ModuleFileResponse(moduleFile.Id, moduleFile.ProjectModuleId, pm.AiModule.Name,
            pm.ProjectId, pm.Project.Name, pm.StepName,
            moduleFile.FileName, moduleFile.ContentType, moduleFile.FileSize, moduleFile.CreatedAt));
    }

    await db.SaveChangesAsync();
    return Results.Ok(result);
}).RequireAuthorization().DisableAntiforgery();

app.MapGet("/api/project-modules/{projectModuleId}/files", async (
    Guid projectModuleId, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var files = await db.ModuleFiles
        .Where(f => f.ProjectModuleId == projectModuleId)
        .OrderByDescending(f => f.CreatedAt)
        .Select(f => new ModuleFileResponse(f.Id, f.ProjectModuleId, f.ProjectModule.AiModule.Name,
            f.ProjectModule.ProjectId, f.ProjectModule.Project.Name, f.ProjectModule.StepName,
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
        .Include(f => f.ProjectModule).ThenInclude(p => p.AiModule)
        .Include(f => f.ProjectModule).ThenInclude(p => p.Project)
        .OrderByDescending(f => f.CreatedAt)
        .Select(f => new ModuleFileResponse(f.Id, f.ProjectModuleId, f.ProjectModule.AiModule.Name,
            f.ProjectModule.ProjectId, f.ProjectModule.Project.Name, f.ProjectModule.StepName,
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

    // Auto-create the mandatory Start module for this pipeline
    var startAiModule = await db.AiModules.FirstOrDefaultAsync(m => m.ModuleType == "Start");
    if (startAiModule is null)
    {
        startAiModule = new AiModule
        {
            Id = Guid.NewGuid(),
            Name = "Inicio",
            Description = "Punto de entrada del pipeline",
            ProviderType = "System",
            ModuleType = "Start",
            ModelName = "start",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.AiModules.Add(startAiModule);
        await db.SaveChangesAsync();
    }

    var startPm = new ProjectModule
    {
        Id = Guid.NewGuid(),
        ProjectId = project.Id,
        AiModuleId = startAiModule.Id,
        StepName = "Inicio",
        IsActive = true,
        PosX = 60,
        PosY = 200,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
    db.ProjectModules.Add(startPm);
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
        .Include(p => p.ProjectModules)
            .ThenInclude(pm => pm.AiModule)
        .Include(p => p.ProjectModules)
            .ThenInclude(pm => pm.OrchestratorOutputs.OrderBy(o => o.SortOrder))
        .FirstOrDefaultAsync(p => p.Id == id);

    if (project is null) return Results.NotFound();

    // Auto-migrate: ensure every project has a Start module
    if (!project.ProjectModules.Any(pm => pm.AiModule.ModuleType == "Start"))
    {
        var startAiModule = await db.AiModules.FirstOrDefaultAsync(m => m.ModuleType == "Start");
        if (startAiModule is null)
        {
            startAiModule = new AiModule
            {
                Id = Guid.NewGuid(),
                Name = "Inicio",
                Description = "Punto de entrada del pipeline",
                ProviderType = "System",
                ModuleType = "Start",
                ModelName = "start",
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.AiModules.Add(startAiModule);
            await db.SaveChangesAsync();
        }

        var startPm = new ProjectModule
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            AiModuleId = startAiModule.Id,
            StepName = "Inicio",
            IsActive = true,
            PosX = 60,
            PosY = 200,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.ProjectModules.Add(startPm);
        await db.SaveChangesAsync();

        // Re-include the new module
        await db.Entry(startPm).Reference(x => x.AiModule).LoadAsync();
        project.ProjectModules.Add(startPm);
    }

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
            pm.AiModule.ModuleType, effectiveModel, pm.StepName,
            pm.Configuration, pm.IsActive,
            pm.PosX, pm.PosY,
            pm.AiModule.ModuleType == "Orchestrator"
                ? pm.OrchestratorOutputs.Select(o => new OrchestratorOutputResponse(
                    o.Id, o.OutputKey, o.Label, o.Prompt, o.DataType, o.SortOrder)).ToList()
                : null);
    }).ToList();

    var connections = await db.ModuleConnections
        .Where(c => c.ProjectId == id)
        .Select(c => new ModuleConnectionResponse(c.Id, c.FromModuleId, c.FromPort, c.ToModuleId, c.ToPort, c.Format))
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

// Save full graph: node positions, connections and module configuration.
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
    Console.WriteLine($"[SaveGraph] Removing {oldConnections.Count} old connections, inserting {req.Connections.Count} new");
    db.ModuleConnections.RemoveRange(oldConnections);

    var moduleIds = modules.Select(m => m.Id).ToHashSet();
    var insertedCount = 0;
    foreach (var conn in req.Connections)
    {
        if (!moduleIds.Contains(conn.FromModuleId) || !moduleIds.Contains(conn.ToModuleId))
        {
            Console.WriteLine($"[SaveGraph] Skipping connection {conn.FromPort}→{conn.ToPort}: module not found");
            continue;
        }
        db.ModuleConnections.Add(new ModuleConnection
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            FromModuleId = conn.FromModuleId,
            FromPort = conn.FromPort,
            ToModuleId = conn.ToModuleId,
            ToPort = conn.ToPort,
            Format = string.IsNullOrWhiteSpace(conn.Format) ? null : conn.Format,
            CreatedAt = now
        });
        insertedCount++;
    }
    Console.WriteLine($"[SaveGraph] Inserted {insertedCount} connections");
    // Persist scene counts into module Configuration
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

            // Empty value: remove key from config
            if (string.IsNullOrEmpty(mc.Value))
            {
                configDict.Remove(mc.Key);
            }
            // Parse value: try bool, then int, then JSON array/object, then string
            else if (bool.TryParse(mc.Value, out var boolVal))
                configDict[mc.Key] = boolVal;
            else if (int.TryParse(mc.Value, out var intVal))
                configDict[mc.Key] = intVal;
            else if (mc.Value.Length > 1 && (mc.Value[0] == '[' || mc.Value[0] == '{'))
            {
                try { configDict[mc.Key] = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(mc.Value); }
                catch { configDict[mc.Key] = mc.Value; }
            }
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
        InstagramConnectionId = source.InstagramConnectionId,
        TikTokConnectionId = source.TikTokConnectionId,
        PinterestConnectionId = source.PinterestConnectionId,
        TelegramConnectionId = source.TelegramConnectionId,
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
            StepName = pm.StepName,
            // When cloning, also exclude systemPrompt to ensure the cloned project
            // gets the latest systemPrompt from the AiModule rather than a frozen copy.
            Configuration = CopyConfigurationExcludingSystemPrompt(pm.Configuration),
            IsActive = pm.IsActive,
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

    // Copy schedule if the source project has one
    var sourceSchedule = await db.ProjectSchedules.FirstOrDefaultAsync(s => s.ProjectId == id);
    if (sourceSchedule is not null)
    {
        db.ProjectSchedules.Add(new ProjectSchedule
        {
            Id = Guid.NewGuid(),
            ProjectId = newProject.Id,
            IsEnabled = sourceSchedule.IsEnabled,
            CronExpression = sourceSchedule.CronExpression,
            TimeZone = sourceSchedule.TimeZone,
            UserInput = sourceSchedule.UserInput,
            UseHistory = sourceSchedule.UseHistory,
            UsePromptQueue = sourceSchedule.UsePromptQueue,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
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
    if (string.Equals(module.ProviderType, "System", StringComparison.OrdinalIgnoreCase)
        && module.ModuleType == "Scene")
        return Results.BadRequest(new { error = "Los modulos de recursos solo pueden ser Archivos o Texto plano." });

    // Prevent adding a second Start module
    if (module.ModuleType == "Start")
    {
        var existingStart = await db.ProjectModules
            .AnyAsync(pm => pm.ProjectId == projectId && pm.AiModule.ModuleType == "Start");
        if (existingStart)
            return Results.BadRequest(new { error = "El pipeline ya tiene un modulo de Inicio. Solo puede existir uno." });
    }

    var pm = new ProjectModule
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        AiModuleId = req.AiModuleId,
        StepName = req.StepName,
        // Inherit the AiModule's Configuration as the initial pipeline-level
        // override so per-module defaults (e.g. numberOfImages, n, modelName)
        // are honored the first time the module is added to a pipeline.
        // IMPORTANT: systemPrompt is excluded to allow dynamic updates from AiModule.
        Configuration = req.Configuration ?? CopyConfigurationExcludingSystemPrompt(module.Configuration),
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
            module.ModuleType, addEffectiveModel, pm.StepName,
            pm.Configuration, pm.IsActive, pm.PosX, pm.PosY));
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

    pm.StepName = req.StepName;
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
        pm.AiModule.ModuleType, updateEffectiveModel, pm.StepName,
        pm.Configuration, pm.IsActive, pm.PosX, pm.PosY));
}).RequireAuthorization();

// Reapunta un nodo del pipeline a otro modulo de catalogo (mismo tipo). Se usa al
// duplicar un modulo compartido con cambios, para que el nodo use la copia nueva.
app.MapPut("/api/projects/{projectId}/modules/{id}/reassign", async (
    Guid projectId, Guid id, ReassignProjectModuleRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var pm = await db.ProjectModules
        .Include(x => x.AiModule)
        .FirstOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId);
    if (pm is null) return Results.NotFound();

    var newModule = await db.AiModules.FindAsync(req.AiModuleId);
    if (newModule is null) return Results.BadRequest(new { error = "El modulo destino no existe." });

    // Solo permitir reapuntar a un modulo del mismo tipo para no romper puertos ni conexiones.
    if (newModule.ModuleType != pm.AiModule.ModuleType)
        return Results.BadRequest(new { error = "El modulo destino debe ser del mismo tipo." });

    pm.AiModuleId = newModule.Id;
    pm.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();

    return Results.Ok(new ProjectModuleResponse(pm.Id, pm.AiModuleId, newModule.Name,
        newModule.ModuleType, newModule.ModelName, pm.StepName,
        pm.Configuration, pm.IsActive, pm.PosX, pm.PosY));
}).RequireAuthorization();

app.MapDelete("/api/projects/{projectId}/modules/{id}", async (
    Guid projectId, Guid id, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var pm = await db.ProjectModules
        .Include(x => x.AiModule)
        .FirstOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId);
    if (pm is null) return Results.NotFound();

    // Prevent deleting the mandatory Start module
    if (pm.AiModule?.ModuleType == "Start")
        return Results.BadRequest(new { error = "No se puede eliminar el modulo de Inicio. Es obligatorio en cada pipeline." });

    // Remove related StepExecutions (and their files via cascade) to avoid FK Restrict violation
    var stepExecutions = await db.StepExecutions
        .Where(se => se.ProjectModuleId == id)
        .ToListAsync();
    if (stepExecutions.Count > 0)
        db.StepExecutions.RemoveRange(stepExecutions);

    db.ProjectModules.Remove(pm);
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

            var execution = await executor.ExecuteAsync(projectId, req.UserInput, bgDb, tenantDbName, ct, req.UseHistory);

            // Load full result for client
            var exec = await bgDb.ProjectExecutions
                .Include(e => e.StepExecutions)
                    .ThenInclude(s => s.Files)
                .Include(e => e.StepExecutions)
                    .ThenInclude(s => s.ProjectModule)
                        .ThenInclude(pm => pm.AiModule)
                .FirstAsync(e => e.Id == execution.Id);

            var steps = exec.StepExecutions.OrderBy(s => s.CreatedAt).Select(s =>
                new StepExecutionResponse(s.Id, s.ProjectModuleId,
                    s.ProjectModule.AiModule.Name, s.ProjectModule.AiModule.ModuleType,
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
        catch (OperationCanceledException)
        {
            // User cancelled the run. The executor already persisted the "Cancelled"
            // status, so just tell the client to refresh — this is not an error.
            Console.WriteLine($"[Pipeline] Execution cancelled by user for project {projectId}");
            await hub.Clients.Group(projectId.ToString())
                .SendAsync("ExecutionCancelled");
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

    var steps = exec.StepExecutions.OrderBy(s => s.CreatedAt).Select(s =>
        new StepExecutionResponse(s.Id, s.ProjectModuleId,
            s.ProjectModule.AiModule.Name, s.ProjectModule.AiModule.ModuleType,
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
        .Select(l => new { l.Level, l.Message, l.ProjectModuleId, l.ModuleName, l.Timestamp })
        .ToListAsync();

    return Results.Ok(logs);
}).RequireAuthorization();

// Retry execution from a specific module and every downstream module.
app.MapPost("/api/executions/{executionId}/retry-from-module", async (
    Guid executionId, RetryFromModuleRequest req, HttpContext ctx,
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

            var execution = await executor.RetryFromModuleAsync(executionId, req.ProjectModuleId, req.Comment, bgDb, tenantDbName, ct);

            var exec = await bgDb.ProjectExecutions
                .Include(e => e.StepExecutions)
                    .ThenInclude(s => s.Files)
                .Include(e => e.StepExecutions)
                    .ThenInclude(s => s.ProjectModule)
                        .ThenInclude(pm => pm.AiModule)
                .FirstAsync(e => e.Id == execution.Id);

            var steps = exec.StepExecutions.OrderBy(s => s.CreatedAt).Select(s =>
                new StepExecutionResponse(s.Id, s.ProjectModuleId,
                    s.ProjectModule.AiModule.Name, s.ProjectModule.AiModule.ModuleType,
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

            var steps = exec.StepExecutions.OrderBy(s => s.CreatedAt).Select(s =>
                new StepExecutionResponse(s.Id, s.ProjectModuleId,
                    s.ProjectModule.AiModule.Name, s.ProjectModule.AiModule.ModuleType,
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

            var steps = exec.StepExecutions.OrderBy(s => s.CreatedAt).Select(s =>
                new StepExecutionResponse(s.Id, s.ProjectModuleId,
                    s.ProjectModule.AiModule.Name, s.ProjectModule.AiModule.ModuleType,
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
app.MapPost("/api/projects/{projectId}/cancel", async (
    Guid projectId, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory,
    ExecutionCancellationService cancellation, IHubContext<ExecutionHub> hub) =>
{
    // 1. Cancel the live token so any in-flight work (provider HTTP calls, polling, …)
    //    is interrupted immediately.
    var cancelled = cancellation.Cancel(projectId);

    // 2. Close out any execution still marked Running/Waiting in the DB — even when there
    //    is NO live token (a run that got stuck before the cancellation fixes, or one left
    //    over after a server restart cleared the in-memory token). Without this, the
    //    "Cancelar" button would appear to "do nothing" on a phantom "En curso" row.
    var dbCancelled = false;
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is not null)
    {
        var active = await db.ProjectExecutions
            .Where(e => e.ProjectId == projectId
                && (e.Status == "Running" || e.Status.StartsWith("Waiting")))
            .ToListAsync();

        if (active.Count > 0)
        {
            var now = DateTime.UtcNow;
            var execIds = active.Select(e => e.Id).ToList();
            foreach (var exec in active)
            {
                exec.Status = "Cancelled";
                exec.CompletedAt = now;
                exec.PausedAtModuleId = null;
                exec.PausedStepData = null;
            }

            var steps = await db.StepExecutions
                .Where(s => execIds.Contains(s.ExecutionId)
                    && (s.Status == "Running" || s.Status.StartsWith("Waiting")))
                .ToListAsync();
            foreach (var s in steps)
            {
                s.Status = "Cancelled";
                s.CompletedAt = now;
            }

            await db.SaveChangesAsync();
            dbCancelled = true;
            await hub.Clients.Group(projectId.ToString()).SendAsync("ExecutionCancelled");
        }
    }

    return Results.Ok(new { cancelled = cancelled || dbCancelled });
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

// ==================== Buffer Image Pool (Permanent URLs) ====================

// Public endpoint for Buffer to access images via permanent URLs
// Supports both GET and HEAD methods (HEAD is required by Buffer's URL validator)
app.MapMethods("/api/public/buffer-image/{slot:int}", new[] { "GET", "HEAD" }, (
    int slot, Server.Services.Instagram.BufferImagePoolService poolService, HttpContext httpContext) =>
{
    var (data, contentType, fileName) = poolService.GetSlotImage(slot);
    
    if (data is null)
        return Results.NotFound();
    
    // Return image inline (display in browser) instead of forcing download
    // This is required by Buffer - they need a static image URL, not a download
    httpContext.Response.Headers.ContentDisposition = "inline";
    return Results.File(data, contentType ?? "image/jpeg");
});

// Diagnostic endpoint to view pool status (authenticated)
app.MapGet("/api/buffer-pool/status", async (
    HttpContext ctx, UserManager<ApplicationUser> um,
    Server.Services.Instagram.BufferImagePoolService poolService) =>
{
    var user = await um.GetUserAsync(ctx.User);
    if (user is null) return Results.Unauthorized();
    
    var slots = poolService.GetAllSlots();
    return Results.Ok(new
    {
        totalSlots = 10,
        occupiedSlots = slots.Count,
        slots = slots.Select(s => new
        {
            s.Slot,
            s.FileName,
            s.OriginalFileName,
            s.ContentType,
            s.FileSize,
            s.CreatedAt,
            url = $"{builder.Configuration["BaseUrl"]?.TrimEnd('/') ?? ""}/api/public/buffer-image/{s.Slot}"
        })
    });
}).RequireAuthorization();

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
        schedule.CronExpression, schedule.TimeZone, schedule.UserInput, schedule.UseHistory, schedule.UsePromptQueue,
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
        UseHistory = req.UseHistory,
        UsePromptQueue = req.UsePromptQueue,
        NextRunAt = nextRun,
        CreatedAt = now,
        UpdatedAt = now
    };

    db.ProjectSchedules.Add(schedule);
    await db.SaveChangesAsync();

    return Results.Ok(new ScheduleResponse(
        schedule.Id, schedule.ProjectId, schedule.IsEnabled,
        schedule.CronExpression, schedule.TimeZone, schedule.UserInput, schedule.UseHistory, schedule.UsePromptQueue,
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
    schedule.UseHistory = req.UseHistory;
    schedule.UsePromptQueue = req.UsePromptQueue;
    schedule.NextRunAt = req.IsEnabled
        ? Server.Services.Scheduler.SchedulerBackgroundService.ComputeNextRun(req.CronExpression, req.TimeZone, now)
        : null;
    schedule.UpdatedAt = now;

    await db.SaveChangesAsync();

    return Results.Ok(new ScheduleResponse(
        schedule.Id, schedule.ProjectId, schedule.IsEnabled,
        schedule.CronExpression, schedule.TimeZone, schedule.UserInput, schedule.UseHistory, schedule.UsePromptQueue,
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

// ==================== Planned Prompts Endpoints ====================

static PlannedPromptResponse ToPlannedPromptResponse(PlannedPrompt p) =>
    new(p.Id, p.ProjectId, p.OrderIndex, p.Content, p.Status,
        p.CreatedAt, p.UpdatedAt, p.UsedAt, p.ExecutionId);

app.MapGet("/api/planner/models", (IPromptPlannerService planner) =>
{
    var models = planner.GetAvailableModels()
        .Select(m => new { provider = m.Provider, modelName = m.ModelName, displayName = m.DisplayName })
        .ToList();
    return Results.Ok(models);
}).RequireAuthorization();

// ── Prompt Builder (asistente para construir prompts de un modulo) ──

app.MapGet("/api/prompt-builder/models", async (
    HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory,
    IPromptBuilderService builderSvc, CancellationToken ct) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var models = await builderSvc.GetAvailableModelsAsync(db, ct);
    return Results.Ok(models
        .Select(m => new { provider = m.Provider, modelName = m.ModelName, displayName = m.DisplayName })
        .ToList());
}).RequireAuthorization();

app.MapPost("/api/prompt-builder/questions", async (
    PromptBuilderQuestionsRequest req, HttpContext ctx, UserManager<ApplicationUser> um,
    ITenantDbContextFactory factory, IPromptBuilderService builderSvc, CancellationToken ct) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var result = await builderSvc.GenerateQuestionsAsync(
        db, req.ModelName, req.TargetKind, req.Description, ct);
    if (!result.Success)
        return Results.BadRequest(new { error = result.Error });

    return Results.Ok(new { questions = result.Questions });
}).RequireAuthorization();

app.MapPost("/api/prompt-builder/compose", async (
    PromptBuilderComposeRequest req, HttpContext ctx, UserManager<ApplicationUser> um,
    ITenantDbContextFactory factory, IPromptBuilderService builderSvc, CancellationToken ct) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var answers = (req.Answers ?? new())
        .Select(a => new PromptBuilderQa(a.Question ?? "", a.Answer ?? ""))
        .ToList();

    var result = await builderSvc.ComposeAsync(
        db, req.ModelName, req.TargetKind, req.Description, answers, ct);
    if (!result.Success)
        return Results.BadRequest(new { error = result.Error });

    return Results.Ok(new { prompt = result.Prompt });
}).RequireAuthorization();

app.MapPost("/api/prompt-builder/add", async (
    PromptBuilderAddRequest req, HttpContext ctx, UserManager<ApplicationUser> um,
    ITenantDbContextFactory factory, IPromptBuilderService builderSvc, CancellationToken ct) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var result = await builderSvc.AddAsync(
        db, req.ModelName, req.TargetKind, req.CurrentPrompt ?? "", req.Addition, ct);
    if (!result.Success)
        return Results.BadRequest(new { error = result.Error });

    return Results.Ok(new { prompt = result.Prompt, warnings = result.Warnings });
}).RequireAuthorization();

app.MapGet("/api/projects/{projectId:guid}/planned-prompts", async (
    Guid projectId, HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects.FindAsync(projectId);
    if (project is null) return Results.NotFound();

    var items = await db.PlannedPrompts
        .Where(p => p.ProjectId == projectId)
        .OrderBy(p => p.OrderIndex)
        .ThenBy(p => p.CreatedAt)
        .ToListAsync();

    return Results.Ok(items.Select(ToPlannedPromptResponse).ToList());
}).RequireAuthorization();

app.MapPost("/api/projects/{projectId:guid}/planned-prompts/generate", async (
    Guid projectId, GeneratePlannedPromptsRequest req,
    HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory,
    IPromptPlannerService planner, CancellationToken ct) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects.FindAsync(projectId);
    if (project is null) return Results.NotFound();

    var result = await planner.GenerateAsync(db, projectId, req.ModelName, req.Count, req.Instructions, ct);
    if (!result.Success)
        return Results.BadRequest(new { error = result.Error });

    var now = DateTime.UtcNow;

    if (req.ReplaceExisting)
    {
        var pending = await db.PlannedPrompts
            .Where(p => p.ProjectId == projectId && p.Status == PlannedPromptStatus.Pending)
            .ToListAsync(ct);
        db.PlannedPrompts.RemoveRange(pending);
    }

    var maxOrder = await db.PlannedPrompts
        .Where(p => p.ProjectId == projectId)
        .Select(p => (int?)p.OrderIndex)
        .MaxAsync(ct) ?? -1;

    var created = new List<PlannedPrompt>();
    var idx = maxOrder + 1;
    foreach (var content in result.Prompts)
    {
        var entity = new PlannedPrompt
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OrderIndex = idx++,
            Content = content,
            Status = PlannedPromptStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.PlannedPrompts.Add(entity);
        created.Add(entity);
    }

    await db.SaveChangesAsync(ct);

    return Results.Ok(created.Select(ToPlannedPromptResponse).ToList());
}).RequireAuthorization();

app.MapPost("/api/projects/{projectId:guid}/planned-prompts", async (
    Guid projectId, CreatePlannedPromptRequest req,
    HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(req.Content))
        return Results.BadRequest(new { error = "Content vacio" });

    var project = await db.Projects.FindAsync(projectId);
    if (project is null) return Results.NotFound();

    var maxOrder = await db.PlannedPrompts
        .Where(p => p.ProjectId == projectId)
        .Select(p => (int?)p.OrderIndex)
        .MaxAsync() ?? -1;

    var now = DateTime.UtcNow;
    var entity = new PlannedPrompt
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        OrderIndex = maxOrder + 1,
        Content = req.Content.Trim(),
        Status = PlannedPromptStatus.Pending,
        CreatedAt = now,
        UpdatedAt = now,
    };

    db.PlannedPrompts.Add(entity);
    await db.SaveChangesAsync();

    return Results.Ok(ToPlannedPromptResponse(entity));
}).RequireAuthorization();

app.MapPut("/api/projects/{projectId:guid}/planned-prompts/{promptId:guid}", async (
    Guid projectId, Guid promptId, UpdatePlannedPromptRequest req,
    HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var prompt = await db.PlannedPrompts
        .FirstOrDefaultAsync(p => p.Id == promptId && p.ProjectId == projectId);
    if (prompt is null) return Results.NotFound();

    if (prompt.Status != PlannedPromptStatus.Pending)
        return Results.BadRequest(new { error = "Solo se pueden editar prompts pendientes" });

    if (string.IsNullOrWhiteSpace(req.Content))
        return Results.BadRequest(new { error = "Content vacio" });

    prompt.Content = req.Content.Trim();
    prompt.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(ToPlannedPromptResponse(prompt));
}).RequireAuthorization();

app.MapDelete("/api/projects/{projectId:guid}/planned-prompts/{promptId:guid}", async (
    Guid projectId, Guid promptId,
    HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var prompt = await db.PlannedPrompts
        .FirstOrDefaultAsync(p => p.Id == promptId && p.ProjectId == projectId);
    if (prompt is null) return Results.NotFound();

    db.PlannedPrompts.Remove(prompt);
    await db.SaveChangesAsync();

    return Results.Ok(new { message = "Prompt eliminado" });
}).RequireAuthorization();

app.MapPost("/api/projects/{projectId:guid}/planned-prompts/reorder", async (
    Guid projectId, ReorderPlannedPromptsRequest req,
    HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var prompts = await db.PlannedPrompts
        .Where(p => p.ProjectId == projectId)
        .ToListAsync();

    var now = DateTime.UtcNow;
    var dict = prompts.ToDictionary(p => p.Id);

    for (var i = 0; i < req.OrderedIds.Count; i++)
    {
        if (dict.TryGetValue(req.OrderedIds[i], out var p))
        {
            p.OrderIndex = i;
            p.UpdatedAt = now;
        }
    }

    await db.SaveChangesAsync();

    var updated = prompts
        .OrderBy(p => p.OrderIndex)
        .ThenBy(p => p.CreatedAt)
        .Select(ToPlannedPromptResponse)
        .ToList();

    return Results.Ok(updated);
}).RequireAuthorization();

app.MapPost("/api/projects/{projectId:guid}/planned-prompts/{promptId:guid}/execute", async (
    Guid projectId, Guid promptId,
    HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory,
    ExecutionCancellationService cancellation, IHubContext<ExecutionHub> hub) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var prompt = await db.PlannedPrompts
        .FirstOrDefaultAsync(p => p.Id == promptId && p.ProjectId == projectId);
    if (prompt is null) return Results.NotFound();

    if (prompt.Status != PlannedPromptStatus.Pending)
        return Results.BadRequest(new { error = "Solo se pueden ejecutar prompts pendientes" });

    var user = await um.GetUserAsync(ctx.User);
    var claims = await um.GetClaimsAsync(user!);
    var tenantDbName = claims.First(c => c.Type == "db_name").Value;

    var content = prompt.Content;
    var token = cancellation.Register(projectId);
    var now = DateTime.UtcNow;

    prompt.Status = PlannedPromptStatus.Used;
    prompt.UsedAt = now;
    prompt.UpdatedAt = now;
    await db.SaveChangesAsync();

    var snapshot = ToPlannedPromptResponse(prompt);

    _ = Task.Run(async () =>
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var bgFactory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
            await using var bgDb = bgFactory.Create(tenantDbName);
            var executor = scope.ServiceProvider.GetRequiredService<IPipelineExecutor>();

            var execution = await executor.ExecuteAsync(projectId, content, bgDb, tenantDbName, token, useHistory: true);

            var trackedPrompt = await bgDb.PlannedPrompts.FirstOrDefaultAsync(p => p.Id == promptId);
            if (trackedPrompt is not null)
            {
                trackedPrompt.ExecutionId = execution.Id;
                trackedPrompt.UpdatedAt = DateTime.UtcNow;
                await bgDb.SaveChangesAsync();
            }

            var exec = await bgDb.ProjectExecutions
                .Include(e => e.StepExecutions)
                    .ThenInclude(s => s.Files)
                .Include(e => e.StepExecutions)
                    .ThenInclude(s => s.ProjectModule)
                        .ThenInclude(pm => pm.AiModule)
                .FirstAsync(e => e.Id == execution.Id);

            var steps = exec.StepExecutions.OrderBy(s => s.CreatedAt).Select(s =>
                new StepExecutionResponse(s.Id, s.ProjectModuleId,
                    s.ProjectModule.AiModule.Name, s.ProjectModule.AiModule.ModuleType,
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
                await hub.Clients.Group(projectId.ToString()).SendAsync("OrchestratorWaitingForReview", detail);
            else if (exec.Status == "WaitingForCheckpoint")
                await hub.Clients.Group(projectId.ToString()).SendAsync("CheckpointWaitingForReview", detail);
            else
                await hub.Clients.Group(projectId.ToString()).SendAsync("ExecutionCompleted", detail);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Pipeline] Planned-prompt execution failed for project {projectId}: {ex}");

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

            await hub.Clients.Group(projectId.ToString()).SendAsync("ExecutionFailed", ex.Message);
        }
        finally
        {
            cancellation.Remove(projectId);
        }
    });

    return Results.Accepted(value: snapshot);
}).RequireAuthorization();


// ==================== Buffer Channels Endpoint ====================

app.MapGet("/api/buffer/channels", async (
    string apiKey, HttpContext ctx,
    UserManager<ApplicationUser> um,
    Server.Services.Instagram.BufferService bufferService) =>
{
    var user = await um.GetUserAsync(ctx.User);
    if (user is null) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.BadRequest(new { error = "apiKey requerida" });

    try
    {
        var channels = await bufferService.GetChannelsAsync(apiKey);
        return Results.Ok(channels);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

// ==================== Social Connections (Redes sociales) ====================

var socialPlatforms = new[] { "instagram", "tiktok", "pinterest" };

// Registra o elimina el webhook de Telegram para un bot token segun haya o no URL publica.
async Task EnsureTelegramWebhookAsync(Server.Services.Telegram.TelegramService telegram, string botToken)
{
    if (string.IsNullOrWhiteSpace(botToken)) return;
    var baseUrl = builder.Configuration["Telegram:WebhookBaseUrl"]
        ?? builder.Configuration["BaseUrl"]
        ?? "";
    try
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            await telegram.DeleteWebhookAsync(botToken);
        else
            await telegram.SetWebhookAsync(botToken, $"{baseUrl.TrimEnd('/')}/api/webhooks/telegram");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning: Could not set Telegram webhook: {ex.Message}");
    }
}

// Asegura que el modulo sentinel (catalogo) exista para una plataforma de publicacion.
static async Task EnsurePublishSentinelAsync(UserDbContext db, string platform)
{
    var (name, desc) = platform switch
    {
        "tiktok" => ("TikTok Publish", "Publica contenido en TikTok (videos e imagenes) via Buffer."),
        "pinterest" => ("Pinterest Publish", "Publica contenido en Pinterest (imagenes) via Buffer."),
        _ => ("Buffer Publish", "Publica contenido en redes sociales (Instagram, etc.) via Buffer."),
    };
    if (await db.AiModules.AnyAsync(m => m.ModuleType == "Publish" && m.ModelName == platform)) return;
    db.AiModules.Add(new AiModule
    {
        Id = Guid.NewGuid(),
        Name = name,
        Description = desc,
        ProviderType = "System",
        ModuleType = "Publish",
        ModelName = platform,
        IsEnabled = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    });
}

app.MapGet("/api/social-connections", async (
    HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var conns = await db.SocialConnections.OrderByDescending(c => c.CreatedAt).ToListAsync();
    var refs = await db.Projects
        .Select(p => new { p.InstagramConnectionId, p.TikTokConnectionId, p.PinterestConnectionId })
        .ToListAsync();

    var result = conns.Select(c => new SocialConnectionResponse(
        c.Id, c.Name, c.Platform, c.ChannelId, c.ChannelName, c.CreatedAt, c.UpdatedAt,
        refs.Count(r => r.InstagramConnectionId == c.Id || r.TikTokConnectionId == c.Id || r.PinterestConnectionId == c.Id)));
    return Results.Ok(result);
}).RequireAuthorization();

app.MapPost("/api/social-connections", async (
    CreateSocialConnectionRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var platform = (req.Platform ?? "").Trim().ToLowerInvariant();
    if (!socialPlatforms.Contains(platform))
        return Results.BadRequest(new { error = "Plataforma invalida. Usa instagram, tiktok o pinterest." });
    if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.ApiKey) || string.IsNullOrWhiteSpace(req.ChannelId))
        return Results.BadRequest(new { error = "Nombre, API Key y canal son obligatorios." });

    var conn = new SocialConnection
    {
        Id = Guid.NewGuid(),
        Name = req.Name.Trim(),
        Platform = platform,
        ApiKey = req.ApiKey.Trim(),
        ChannelId = req.ChannelId.Trim(),
        ChannelName = req.ChannelName,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
    db.SocialConnections.Add(conn);
    await EnsurePublishSentinelAsync(db, platform);
    await db.SaveChangesAsync();

    return Results.Created($"/api/social-connections/{conn.Id}", new SocialConnectionResponse(
        conn.Id, conn.Name, conn.Platform, conn.ChannelId, conn.ChannelName, conn.CreatedAt, conn.UpdatedAt, 0));
}).RequireAuthorization();

app.MapPut("/api/social-connections/{id:guid}", async (
    Guid id, UpdateSocialConnectionRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var conn = await db.SocialConnections.FindAsync(id);
    if (conn is null) return Results.NotFound();

    var platform = (req.Platform ?? "").Trim().ToLowerInvariant();
    if (!socialPlatforms.Contains(platform))
        return Results.BadRequest(new { error = "Plataforma invalida. Usa instagram, tiktok o pinterest." });
    if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.ChannelId))
        return Results.BadRequest(new { error = "Nombre y canal son obligatorios." });

    conn.Name = req.Name.Trim();
    conn.Platform = platform;
    conn.ChannelId = req.ChannelId.Trim();
    conn.ChannelName = req.ChannelName;
    if (!string.IsNullOrWhiteSpace(req.ApiKey))
        conn.ApiKey = req.ApiKey.Trim();
    conn.UpdatedAt = DateTime.UtcNow;

    await EnsurePublishSentinelAsync(db, platform);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Conexion actualizada" });
}).RequireAuthorization();

app.MapDelete("/api/social-connections/{id:guid}", async (
    Guid id, HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var conn = await db.SocialConnections.FindAsync(id);
    if (conn is null) return Results.NotFound();

    db.SocialConnections.Remove(conn);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

// ==================== Messaging Connections (Mensajeria) ====================

app.MapGet("/api/messaging-connections", async (
    HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var conns = await db.MessagingConnections.OrderByDescending(c => c.CreatedAt).ToListAsync();
    var refs = await db.Projects.Select(p => p.TelegramConnectionId).ToListAsync();

    var result = conns.Select(c => new MessagingConnectionResponse(
        c.Id, c.Name, c.Provider, c.ChatId, c.CreatedAt, c.UpdatedAt,
        refs.Count(r => r == c.Id)));
    return Results.Ok(result);
}).RequireAuthorization();

app.MapPost("/api/messaging-connections", async (
    CreateMessagingConnectionRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory,
    Server.Services.Telegram.TelegramService telegram) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var provider = (req.Provider ?? "telegram").Trim().ToLowerInvariant();
    if (provider != "telegram")
        return Results.BadRequest(new { error = "Proveedor de mensajeria no soportado." });
    if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.BotToken) || string.IsNullOrWhiteSpace(req.ChatId))
        return Results.BadRequest(new { error = "Nombre, bot token y chat id son obligatorios." });

    var conn = new MessagingConnection
    {
        Id = Guid.NewGuid(),
        Name = req.Name.Trim(),
        Provider = provider,
        BotToken = req.BotToken.Trim(),
        ChatId = req.ChatId.Trim(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
    db.MessagingConnections.Add(conn);

    // Ensure the Telegram Interaction sentinel module exists
    if (!await db.AiModules.AnyAsync(m => m.ModuleType == "Interaction" && m.ModelName == "telegram"))
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
    await EnsureTelegramWebhookAsync(telegram, conn.BotToken);

    return Results.Created($"/api/messaging-connections/{conn.Id}", new MessagingConnectionResponse(
        conn.Id, conn.Name, conn.Provider, conn.ChatId, conn.CreatedAt, conn.UpdatedAt, 0));
}).RequireAuthorization();

app.MapPut("/api/messaging-connections/{id:guid}", async (
    Guid id, UpdateMessagingConnectionRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory,
    Server.Services.Telegram.TelegramService telegram) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var conn = await db.MessagingConnections.FindAsync(id);
    if (conn is null) return Results.NotFound();

    if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.ChatId))
        return Results.BadRequest(new { error = "Nombre y chat id son obligatorios." });

    conn.Name = req.Name.Trim();
    conn.ChatId = req.ChatId.Trim();
    if (!string.IsNullOrWhiteSpace(req.BotToken))
        conn.BotToken = req.BotToken.Trim();
    conn.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();
    await EnsureTelegramWebhookAsync(telegram, conn.BotToken);
    return Results.Ok(new { message = "Conexion actualizada" });
}).RequireAuthorization();

app.MapDelete("/api/messaging-connections/{id:guid}", async (
    Guid id, HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var conn = await db.MessagingConnections.FindAsync(id);
    if (conn is null) return Results.NotFound();

    db.MessagingConnections.Remove(conn);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

// ==================== Shopify Connections ====================

// Asegura que el modulo sentinel "Shopify Blog" exista en el catalogo.
static async Task EnsureShopifyBlogSentinelAsync(UserDbContext db)
{
    if (await db.AiModules.AnyAsync(m => m.ModuleType == "ShopifyBlog")) return;
    db.AiModules.Add(new AiModule
    {
        Id = Guid.NewGuid(),
        Name = "Shopify Blog",
        Description = "Publica un articulo de blog en una tienda Shopify.",
        ProviderType = "System",
        ModuleType = "ShopifyBlog",
        ModelName = "shopify-blog",
        IsEnabled = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    });
}

app.MapGet("/api/shopify-connections", async (
    HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var conns = await db.ShopifyConnections.OrderByDescending(c => c.CreatedAt).ToListAsync();
    var refs = await db.Projects.Select(p => p.ShopifyConnectionId).ToListAsync();

    var result = conns.Select(c => new ShopifyConnectionResponse(
        c.Id, c.Name, c.ShopDomain, c.CreatedAt, c.UpdatedAt, refs.Count(r => r == c.Id)));
    return Results.Ok(result);
}).RequireAuthorization();

app.MapPost("/api/shopify-connections", async (
    CreateShopifyConnectionRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.ShopDomain)
        || string.IsNullOrWhiteSpace(req.ClientId) || string.IsNullOrWhiteSpace(req.ClientSecret))
        return Results.BadRequest(new { error = "Nombre, dominio, Client ID y Client Secret son obligatorios." });

    var conn = new ShopifyConnection
    {
        Id = Guid.NewGuid(),
        Name = req.Name.Trim(),
        ShopDomain = Server.Services.Shopify.ShopifyService.NormalizeDomain(req.ShopDomain),
        ClientId = req.ClientId.Trim(),
        ClientSecret = req.ClientSecret.Trim(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
    db.ShopifyConnections.Add(conn);
    await EnsureShopifyBlogSentinelAsync(db);
    await db.SaveChangesAsync();

    return Results.Created($"/api/shopify-connections/{conn.Id}", new ShopifyConnectionResponse(
        conn.Id, conn.Name, conn.ShopDomain, conn.CreatedAt, conn.UpdatedAt, 0));
}).RequireAuthorization();

app.MapPut("/api/shopify-connections/{id:guid}", async (
    Guid id, UpdateShopifyConnectionRequest req, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var conn = await db.ShopifyConnections.FindAsync(id);
    if (conn is null) return Results.NotFound();

    if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.ShopDomain))
        return Results.BadRequest(new { error = "Nombre y dominio son obligatorios." });

    conn.Name = req.Name.Trim();
    conn.ShopDomain = Server.Services.Shopify.ShopifyService.NormalizeDomain(req.ShopDomain);
    if (!string.IsNullOrWhiteSpace(req.ClientId))
        conn.ClientId = req.ClientId.Trim();
    if (!string.IsNullOrWhiteSpace(req.ClientSecret))
        conn.ClientSecret = req.ClientSecret.Trim();
    conn.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Conexion actualizada" });
}).RequireAuthorization();

app.MapDelete("/api/shopify-connections/{id:guid}", async (
    Guid id, HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var conn = await db.ShopifyConnections.FindAsync(id);
    if (conn is null) return Results.NotFound();

    db.ShopifyConnections.Remove(conn);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

// Lista los blogs de la tienda Shopify asignada al proyecto (para elegir destino en el nodo).
app.MapGet("/api/projects/{projectId:guid}/shopify/blogs", async (
    Guid projectId, HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory,
    Server.Services.Shopify.ShopifyService shopify) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects.FindAsync(projectId);
    if (project is null) return Results.NotFound();
    if (project.ShopifyConnectionId is null)
        return Results.BadRequest(new { error = "El proyecto no tiene una conexion de Shopify asignada." });

    var conn = await db.ShopifyConnections.FindAsync(project.ShopifyConnectionId.Value);
    if (conn is null)
        return Results.BadRequest(new { error = "La conexion de Shopify asignada ya no existe." });

    try
    {
        var blogs = await shopify.ListBlogsAsync(conn.ShopDomain, conn.ClientId, conn.ClientSecret, ctx.RequestAborted);
        return Results.Ok(blogs);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

// ==================== Project ↔ Connections assignment ====================

app.MapGet("/api/projects/{projectId:guid}/connections", async (
    Guid projectId, HttpContext ctx, UserManager<ApplicationUser> um, ITenantDbContextFactory factory) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects.FindAsync(projectId);
    if (project is null) return Results.NotFound();

    return Results.Ok(new ProjectConnectionsDto(
        project.InstagramConnectionId, project.TikTokConnectionId,
        project.PinterestConnectionId, project.TelegramConnectionId,
        project.ShopifyConnectionId));
}).RequireAuthorization();

app.MapPut("/api/projects/{projectId:guid}/connections", async (
    Guid projectId, ProjectConnectionsDto dto, HttpContext ctx,
    UserManager<ApplicationUser> um, ITenantDbContextFactory factory,
    Server.Services.Telegram.TelegramService telegram) =>
{
    await using var db = await ResolveTenantDb(ctx, um, factory);
    if (db is null) return Results.Unauthorized();

    var project = await db.Projects.FindAsync(projectId);
    if (project is null) return Results.NotFound();

    // Validate that each assigned connection exists and matches its slot.
    async Task<bool> ValidSocialAsync(Guid? connId, string platform)
    {
        if (connId is null) return true;
        var c = await db.SocialConnections.FindAsync(connId.Value);
        return c is not null && c.Platform == platform;
    }

    if (!await ValidSocialAsync(dto.InstagramConnectionId, "instagram")
        || !await ValidSocialAsync(dto.TikTokConnectionId, "tiktok")
        || !await ValidSocialAsync(dto.PinterestConnectionId, "pinterest"))
        return Results.BadRequest(new { error = "Conexion social invalida para la plataforma." });

    MessagingConnection? tgConn = null;
    if (dto.TelegramConnectionId is not null)
    {
        tgConn = await db.MessagingConnections.FindAsync(dto.TelegramConnectionId.Value);
        if (tgConn is null || tgConn.Provider != "telegram")
            return Results.BadRequest(new { error = "Conexion de Telegram invalida." });
    }

    if (dto.ShopifyConnectionId is not null
        && await db.ShopifyConnections.FindAsync(dto.ShopifyConnectionId.Value) is null)
        return Results.BadRequest(new { error = "Conexion de Shopify invalida." });

    project.InstagramConnectionId = dto.InstagramConnectionId;
    project.TikTokConnectionId = dto.TikTokConnectionId;
    project.PinterestConnectionId = dto.PinterestConnectionId;
    project.TelegramConnectionId = dto.TelegramConnectionId;
    project.ShopifyConnectionId = dto.ShopifyConnectionId;
    project.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();

    if (tgConn is not null)
        await EnsureTelegramWebhookAsync(telegram, tgConn.BotToken);

    return Results.Ok(new { message = "Conexiones del proyecto actualizadas" });
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
