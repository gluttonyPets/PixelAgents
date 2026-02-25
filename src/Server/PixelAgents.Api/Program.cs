using PixelAgents.AgentSystem.Abstractions;
using PixelAgents.AgentSystem.Discovery;
using PixelAgents.Application;
using PixelAgents.Application.Common.Interfaces;
using PixelAgents.Infrastructure;
using PixelAgents.Infrastructure.Persistence;
using PixelAgents.Infrastructure.Services;
using PixelAgents.Module.ContentWeaver;
using PixelAgents.Module.ScheduleMaster;
using PixelAgents.Module.TemplateForge;
using PixelAgents.Module.TrendScout;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog
builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .WriteTo.Console());

// Application & Infrastructure layers
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Agent System
builder.Services.AddAgentSystem();
builder.Services.AddAgentModule<TrendScoutModule>();
builder.Services.AddAgentModule<TemplateForgeModule>();
builder.Services.AddAgentModule<ContentWeaverModule>();
builder.Services.AddAgentModule<ScheduleMasterModule>();

// SignalR
builder.Services.AddSignalR();
builder.Services.AddScoped<IAgentNotificationService, AgentNotificationService>();

// API
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "PixelAgents API", Version = "v1" });
});

// CORS for Blazor WASM client
builder.Services.AddCors(options =>
{
    options.AddPolicy("BlazorClient", policy =>
    {
        policy.WithOrigins(
                builder.Configuration.GetValue<string>("ClientUrl") ?? "https://localhost:7100")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

// Initialize database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.EnsureCreatedAsync();

    // Register modules in the registry
    var registry = scope.ServiceProvider.GetRequiredService<AgentModuleRegistry>();
    var modules = scope.ServiceProvider.GetServices<IAgentModule>();
    foreach (var module in modules)
    {
        registry.Register(module);
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("BlazorClient");
app.UseHttpsRedirection();
app.MapControllers();
app.MapHub<AgentHub>("/hubs/agents");

app.Run();
