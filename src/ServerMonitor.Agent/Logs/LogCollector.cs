namespace ServerMonitor.Agent.Logs;

internal sealed partial class LogCollector(
    IReadOnlyList<ILogSource> sources,
    ILogQueue queue,
    TimeProvider time,
    ILogger<LogCollector> logger)
    : BackgroundService
{
    public const int MaxEntriesPerRead = 500;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (sources.Count == 0)
        {
            LogNoSources(logger);
            return;
        }

        LogStarted(logger, string.Join(", ", sources.Select(s => s.Key)));

        using var timer = new PeriodicTimer(PollInterval, time);
        do
        {
            foreach (var source in sources)
            {
                await PollAsync(source, stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task PollAsync(ILogSource source, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var position = await queue.GetPositionAsync(source.Key, ct);
                var result = await source.ReadAsync(position, MaxEntriesPerRead, ct);
                if (result is null)
                {
                    return;
                }

                await queue.AppendAsync(source.Key, result.Entries, result.Position, ct);
                if (result.Entries.Count < MaxEntriesPerRead)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSourceFailed(logger, ex, source.Key);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Collecting logs from {Sources}")]
    private static partial void LogStarted(ILogger logger, string sources);

    [LoggerMessage(Level = LogLevel.Information, Message = "No log sources configured (Agent:Logs)")]
    private static partial void LogNoSources(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading logs from {Source} failed")]
    private static partial void LogSourceFailed(ILogger logger, Exception exception, string source);
}
