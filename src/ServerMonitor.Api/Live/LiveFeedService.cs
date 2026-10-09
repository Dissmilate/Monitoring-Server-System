using Confluent.Kafka;
using Google.Protobuf;
using Microsoft.AspNetCore.SignalR;
using ServerMonitor.Contracts.Live;
using ServerMonitor.Contracts.Metrics;
using ServerMonitor.Infrastructure.Messaging;
using ServerMonitor.Protocol.Messaging.V1;
using ServerMonitor.Protocol.Telemetry.V1;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Api.Live;

internal sealed partial class LiveFeedService(
    KafkaConsumerFactory consumers,
    IHubContext<LiveUpdatesHub, ILiveClient> hub,
    ILogger<LiveFeedService> logger)
    : BackgroundService
{
    public static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(5);

    private readonly Dictionary<Guid, DateTimeOffset> _lastSummary = [];

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() => RunAsync(stoppingToken), stoppingToken);

    private async Task RunAsync(CancellationToken ct)
    {
        using var consumer = await ConnectAsync(ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]> result;
                try
                {
                    result = consumer.Consume(ct);
                }
                catch (ConsumeException ex) when (!ex.Error.IsFatal)
                {
                    LogConsumeFailed(logger, ex.Error.Reason);
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                try
                {
                    if (result.Topic == KafkaTopics.Metrics)
                    {
                        await ForwardMetricsAsync(MetricBatchEnvelope.Parser.ParseFrom(result.Message.Value));
                    }
                    else
                    {
                        await ForwardEventAsync(MonitorEvent.Parser.ParseFrom(result.Message.Value));
                    }
                }
                catch (Exception ex) when (ex is InvalidProtocolBufferException or FormatException)
                {
                    LogUndecodable(logger, ex, result.TopicPartitionOffset.ToString());
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task<IConsumer<string, byte[]>> ConnectAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                return consumers.CreateBroadcast(KafkaTopics.Metrics, KafkaTopics.MonitorEvents);
            }
            catch (KafkaException ex)
            {
                LogConsumeFailed(logger, ex.Error.Reason);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task ForwardMetricsAsync(MetricBatchEnvelope envelope)
    {
        var serverId = Guid.Parse(envelope.ServerId);
        var watchers = hub.Clients.Group(LiveUpdatesHub.ServerGroup(serverId));

        MetricSample? newest = null;
        foreach (var sample in envelope.Batch.Samples)
        {
            await watchers.MetricsReceived(ToLive(serverId, sample));
            if (newest is null || sample.Time.ToDateTimeOffset() > newest.Time.ToDateTimeOffset())
            {
                newest = sample;
            }
        }

        if (newest is null)
        {
            return;
        }

        var time = newest.Time.ToDateTimeOffset();
        if (_lastSummary.TryGetValue(serverId, out var last) && time - last < SummaryInterval)
        {
            return;
        }

        _lastSummary[serverId] = time;
        await hub.Clients.All.ServerSummaryUpdated(ToSummary(serverId, newest));
    }

    private async Task ForwardEventAsync(MonitorEvent monitorEvent)
    {
        if (monitorEvent.PayloadCase == MonitorEvent.PayloadOneofCase.AgentStatusChanged)
        {
            await hub.Clients.All.ServerStatusChanged(new ServerStatusChangedDto(
                Guid.Parse(monitorEvent.ServerId),
                monitorEvent.AgentStatusChanged.Status,
                monitorEvent.Time.ToDateTimeOffset()));
        }
    }

    internal static LiveSampleDto ToLive(Guid serverId, MetricSample sample) => new(
        serverId,
        new SystemMetricDto(
            sample.Time.ToDateTimeOffset(),
            sample.CpuPercent,
            sample.CpuPercent,
            sample.MemoryUsedBytes,
            sample.MemoryTotalBytes,
            sample.SwapUsedBytes,
            sample.HasLoadAverage1M ? sample.LoadAverage1M : null,
            sample.UptimeSeconds),
        [.. sample.Disks.Select(d => new LiveDiskDto(d.Mount, d.UsedBytes, d.TotalBytes))],
        [.. sample.Network.Select(n => new LiveNetworkDto(n.Interface, n.RxBytesPerSecond, n.TxBytesPerSecond))]);

    internal static ServerSummaryDto ToSummary(Guid serverId, MetricSample sample) => new(
        serverId,
        sample.Time.ToDateTimeOffset(),
        sample.CpuPercent,
        sample.MemoryUsedBytes,
        sample.MemoryTotalBytes,
        sample.Disks.Where(d => d.TotalBytes > 0).Select(d => (double?)(100.0 * d.UsedBytes / d.TotalBytes)).Max());

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consuming live updates failed: {Reason}; retrying")]
    private static partial void LogConsumeFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping an undecodable message at {Position}")]
    private static partial void LogUndecodable(ILogger logger, Exception exception, string position);
}
