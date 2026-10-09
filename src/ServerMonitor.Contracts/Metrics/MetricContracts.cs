namespace ServerMonitor.Contracts.Metrics;

public sealed record MetricsDto(
    string Resolution,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<SystemMetricDto> System,
    IReadOnlyList<DiskSeriesDto> Disks,
    IReadOnlyList<NetworkSeriesDto> Network);

public sealed record SystemMetricDto(
    DateTimeOffset Time,
    double CpuPercent,
    double CpuPercentMax,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    long SwapUsedBytes,
    double? LoadAverage1m,
    long UptimeSeconds);

public sealed record DiskSeriesDto(string Mount, IReadOnlyList<DiskPointDto> Points);

public sealed record DiskPointDto(DateTimeOffset Time, long UsedBytes, long TotalBytes);

public sealed record NetworkSeriesDto(string Interface, IReadOnlyList<NetworkPointDto> Points);

public sealed record NetworkPointDto(DateTimeOffset Time, double RxBytesPerSecond, double TxBytesPerSecond);
