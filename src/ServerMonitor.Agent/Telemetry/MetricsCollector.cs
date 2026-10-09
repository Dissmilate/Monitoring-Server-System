using ServerMonitor.Agent.Control;

namespace ServerMonitor.Agent.Telemetry;

internal sealed partial class MetricsCollector(
    AgentSession session,
    MetricsSampler sampler,
    RuntimeConfiguration configuration,
    TimeProvider time,
    ILogger<MetricsCollector> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await session.WaitForCredentialsAsync(stoppingToken);
        await sampler.PrimeAsync(stoppingToken);

        using var timer = new PeriodicTimer(configuration.Current.MetricsInterval, time);
        LogStarted(logger, sampler.SourceDescription, timer.Period);

        void OnChanged(RuntimeConfig config) => timer.Period = config.MetricsInterval;
        configuration.Changed += OnChanged;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await sampler.SampleAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogCollectFailed(logger, ex);
                }
            }
        }
        finally
        {
            configuration.Changed -= OnChanged;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Collecting metrics from {Source} every {Interval}")]
    private static partial void LogStarted(ILogger logger, string source, TimeSpan interval);

    [LoggerMessage(Level = LogLevel.Error, Message = "Collecting metrics failed")]
    private static partial void LogCollectFailed(ILogger logger, Exception exception);
}
