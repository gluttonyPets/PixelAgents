using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PixelAgents.Application.Common.Interfaces;
using PixelAgents.Domain.Interfaces;
using PixelAgents.Infrastructure.Persistence;
using PixelAgents.Infrastructure.Persistence.Repositories;
using PixelAgents.Infrastructure.Services;

namespace PixelAgents.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite(configuration.GetConnectionString("DefaultConnection") ?? "Data Source=pixelagents.db"));

        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<ApplicationDbContext>());
        services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
        services.AddScoped<IAgentRepository, AgentRepository>();
        services.AddScoped<IPipelineRepository, PipelineRepository>();

        return services;
    }
}
