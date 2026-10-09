using ServerMonitor.Application.Metrics;
using ServerMonitor.Protocol.Telemetry.V1;

namespace ServerMonitor.Workers.Metrics;

internal static class MetricBatchMapper
{
    public static MetricWriteBatch ToWriteBatch(IEnumerable<MetricBatchEnvelope> envelopes)
    {
        var system = new List<SystemMetricPoint>();
        var disks = new List<DiskMetricPoint>();
        var network = new List<NetworkMetricPoint>();

        foreach (var envelope in envelopes)
        {
            var serverId = Guid.Parse(envelope.ServerId);
            foreach (var sample in envelope.Batch.Samples)
            {
                var time = sample.Time.ToDateTimeOffset();

                system.Add(new SystemMetricPoint(
                    serverId,
                    time,
                    sample.CpuPercent,
                    sample.MemoryUsedBytes,
                    sample.MemoryTotalBytes,
                    sample.SwapUsedBytes,
                    sample.HasLoadAverage1M ? sample.LoadAverage1M : null,
                    sample.UptimeSeconds));

                disks.AddRange(sample.Disks.Select(d =>
                    new DiskMetricPoint(serverId, time, d.Mount, d.UsedBytes, d.TotalBytes)));

                network.AddRange(sample.Network.Select(n =>
                    new NetworkMetricPoint(serverId, time, n.Interface, n.RxBytesPerSecond, n.TxBytesPerSecond)));
            }
        }

        return new MetricWriteBatch(system, disks, network);
    }
}
