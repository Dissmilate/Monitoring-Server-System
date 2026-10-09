namespace ServerMonitor.Application.Metrics;

public sealed record SystemMetricPoint(
    Guid ServerId,
    DateTimeOffset Time,
    double CpuPercent,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    long SwapUsedBytes,
    double? LoadAverage1m,
    long UptimeSeconds);

public sealed record DiskMetricPoint(Guid ServerId, DateTimeOffset Time, string Mount, long UsedBytes, long TotalBytes);

public sealed record NetworkMetricPoint(
    Guid ServerId,
    DateTimeOffset Time,
    string Interface,
    double RxBytesPerSecond,
    double TxBytesPerSecond);

public sealed record MetricWriteBatch(
    IReadOnlyList<SystemMetricPoint> System,
    IReadOnlyList<DiskMetricPoint> Disks,
    IReadOnlyList<NetworkMetricPoint> Network)
{
    public bool IsEmpty => System.Count == 0 && Disks.Count == 0 && Network.Count == 0;
}
