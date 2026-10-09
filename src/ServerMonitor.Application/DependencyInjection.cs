using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServerMonitor.Application.Agents;
using ServerMonitor.Application.Logs;
using ServerMonitor.Application.Metrics;
using ServerMonitor.Application.Servers;

namespace ServerMonitor.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<EnrollmentOptions>();

        services.AddScoped<ListServersHandler>();
        services.AddScoped<GetServerHandler>();
        services.AddScoped<CreateServerHandler>();
        services.AddScoped<RenameServerHandler>();
        services.AddScoped<DeleteServerHandler>();
        services.AddScoped<IssueEnrollmentTokenHandler>();
        services.AddScoped<RevokeAgentKeysHandler>();

        services.AddScoped<EnrollAgentHandler>();
        services.AddScoped<AgentAuthenticator>();
        services.AddScoped<AgentConnectionService>();
        services.AddScoped<ExpireStalePresenceHandler>();

        services.AddScoped<GetAgentConfigurationHandler>();
        services.AddScoped<UpdateAgentConfigurationHandler>();
        services.AddScoped<RequestAgentCommandHandler>();
        services.AddScoped<ListAgentCommandsHandler>();
        services.AddScoped<GetAgentCommandHandler>();

        services.AddScoped<GetServerMetricsHandler>();
        services.AddScoped<GetLatestMetricsHandler>();
        services.AddScoped<SearchLogsHandler>();

        return services;
    }
}
