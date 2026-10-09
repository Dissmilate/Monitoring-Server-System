using Grpc.Core;
using ServerMonitor.Agent.Transport;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Telemetry;

internal sealed partial class TelemetrySender(
    AgentSession session,
    ITelemetryBuffer buffer,
    IGatewayClient gateway,
    ILogger<TelemetrySender> logger)
    : BackgroundService
{
    public const int MaxBatchSize = 500;

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
                    await buffer.WaitForDataAsync(IdleWait, stoppingToken);
                }

                backoff.Reset();
            }
            catch (RpcException ex) when (!stoppingToken.IsCancellationRequested)
            {
                var delay = backoff.Next();
                if (ex.StatusCode == StatusCode.Unauthenticated)
                {
                    LogKeyRejected(logger, delay);
                }
                else
                {
                    LogSendFailed(logger, ex.StatusCode, delay);
                }

                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    internal async Task<int> SendNextBatchAsync(string apiKey, CancellationToken ct)
    {
        var pending = await buffer.PeekAsync(MaxBatchSize, ct);
        if (pending.Count == 0)
        {
            return 0;
        }

        var batch = new MetricBatch { BatchId = Guid.CreateVersion7().ToString() };
        batch.Samples.Add(pending.Select(p => p.Sample));

        var accepted = await gateway.PushMetricsAsync(apiKey, batch, ct);
        await buffer.RemoveThroughAsync(pending[^1].Id, ct);

        if (accepted < pending.Count)
        {
            LogSamplesRejected(logger, pending.Count - accepted);
        }

        return pending.Count;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending metrics failed with {StatusCode}; retrying in {Delay}")]
    private static partial void LogSendFailed(ILogger logger, StatusCode statusCode, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "The Gateway rejected the API key (revoked?); metrics stay buffered, retrying in {Delay}")]
    private static partial void LogKeyRejected(ILogger logger, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Gateway discarded {Count} invalid samples (check the agent clock)")]
    private static partial void LogSamplesRejected(ILogger logger, int count);
}
