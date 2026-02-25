using Microsoft.Extensions.DependencyInjection;
using PixelAgents.AgentSystem.Abstractions;
using PixelAgents.AgentSystem.Orchestration;

namespace PixelAgents.AgentSystem.Discovery;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentSystem(this IServiceCollection services)
    {
        services.AddSingleton<AgentModuleRegistry>();
        services.AddScoped<PipelineOrchestrator>();
        return services;
    }

    public static IServiceCollection AddAgentModule<TModule>(this IServiceCollection services)
        where TModule : class, IAgentModule
    {
        services.AddScoped<IAgentModule, TModule>();
        services.AddScoped<TModule>();
        return services;
    }
}
