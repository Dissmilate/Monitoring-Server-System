using Microsoft.Extensions.Options;
using ServerMonitor.Application.Agents;

namespace ServerMonitor.Workers.Agents;

public sealed partial class PresenceMonitorService(
    IServiceScopeFactory scopes,
    IOptions<PresenceOptions> options,
    TimeProvider time,
    ILogger<PresenceMonitorService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var timer = new PeriodicTimer(settings.CheckInterval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var expired = await scope.ServiceProvider.GetRequiredService<ExpireStalePresenceHandler>()
                    .HandleAsync(settings.OfflineAfter, stoppingToken);

                foreach (var serverId in expired)
                {
                    LogWentOffline(logger, serverId, settings.OfflineAfter);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCheckFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {ServerId} went offline: no heartbeat for {OfflineAfter}")]
    private static partial void LogWentOffline(ILogger logger, Guid serverId, TimeSpan offlineAfter);

    [LoggerMessage(Level = LogLevel.Error, Message = "Presence check failed")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);
}
