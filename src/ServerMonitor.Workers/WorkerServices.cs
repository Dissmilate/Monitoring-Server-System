using ServerMonitor.Application;
using ServerMonitor.Infrastructure;
using ServerMonitor.Workers.Agents;
using ServerMonitor.Workers.Logs;
using ServerMonitor.Workers.Metrics;

namespace ServerMonitor.Workers;

public static class WorkerServices
{
    public static IHostApplicationBuilder AddWorkerServices(this IHostApplicationBuilder builder)
    {
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure();
        builder.Services.AddKafka(builder.Configuration);
        builder.Services.AddOpenSearch(builder.Configuration);

        builder.Services.Configure<PresenceOptions>(builder.Configuration.GetSection(PresenceOptions.SectionName));
        builder.Services.Configure<CommandRelayOptions>(builder.Configuration.GetSection(CommandRelayOptions.SectionName));

        builder.Services.AddHostedService<MetricsWriterService>();
        builder.Services.AddHostedService<LogIndexerService>();
        builder.Services.AddHostedService<PresenceMonitorService>();
        builder.Services.AddHostedService<CommandOutboxRelayService>();

        return builder;
    }
}
