using Grpc.Core;
using ServerMonitor.Agent.Transport;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Logs;

internal sealed partial class LogSender(
    AgentSession session,
    ILogQueue queue,
    IGatewayClient gateway,
    ILogger<LogSender> logger)
    : BackgroundService
{
    public const int MaxBatchEntries = 500;

    public const int MaxBatchBytes = 256 * 1024;

    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var credentials = await session.WaitForCredentialsAsync(stoppingToken);
        var backoff = new ExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await SendNextBatchAsync(credentials.ApiKey, stoppingToken) == 0)
                {
                    await queue.WaitForDataAsync(IdleWait, stoppingToken);
                }

                backoff.Reset();
            }
            catch (RpcException ex) when (!stoppingToken.IsCancellationRequested)
            {
                var delay = backoff.Next();
                LogSendFailed(logger, ex.StatusCode, delay);
                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    internal async Task<int> SendNextBatchAsync(string apiKey, CancellationToken ct)
    {
        var pending = await queue.PeekAsync(MaxBatchEntries, MaxBatchBytes, ct);
        if (pending.Count == 0)
        {
            return 0;
        }

        var batch = new LogBatch { BatchId = Guid.CreateVersion7().ToString() };
        batch.Entries.Add(pending.Select(p => p.Entry));

        try
        {
            await gateway.PushLogsAsync(apiKey, batch, ct);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        {
            LogBatchRejected(logger, pending.Count, ex.Status.Detail);
        }

        await queue.RemoveThroughAsync(pending[^1].Id, ct);
        return pending.Count;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending logs failed with {StatusCode}; retrying in {Delay}")]
    private static partial void LogSendFailed(ILogger logger, StatusCode statusCode, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "The Gateway rejected a batch of {Count} log entries: {Detail}")]
    private static partial void LogBatchRejected(ILogger logger, int count, string detail);
}
