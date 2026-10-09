using Microsoft.Extensions.Options;
using ServerMonitor.Application.Abstractions;

namespace ServerMonitor.Workers.Agents;

public sealed partial class CommandOutboxRelayService(
    ICommandOutbox outbox,
    IOptions<CommandRelayOptions> options,
    TimeProvider time,
    ILogger<CommandOutboxRelayService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var timer = new PeriodicTimer(settings.Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                while (await outbox.RelayPendingAsync(settings.BatchSize, stoppingToken) == settings.BatchSize)
                {
                }

                var expired = await outbox.ExpireAsync(time.GetUtcNow(), stoppingToken);
                if (expired > 0)
                {
                    LogExpired(logger, expired);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRelayFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} agent commands expired without a result")]
    private static partial void LogExpired(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Relaying agent commands failed")]
    private static partial void LogRelayFailed(ILogger logger, Exception exception);
}
