using ServerMonitor.Contracts.Metrics;

namespace ServerMonitor.Contracts.Live;

public static class LiveHub
{
    public const string Path = "/hubs/live";

    public const string WatchServer = nameof(WatchServer);

    public const string UnwatchServer = nameof(UnwatchServer);
}

public interface ILiveClient
{
    Task ServerStatusChanged(ServerStatusChangedDto change);

    Task ServerSummaryUpdated(ServerSummaryDto summary);

    Task MetricsReceived(LiveSampleDto sample);
}

public sealed record ServerStatusChangedDto(Guid ServerId, string Status, DateTimeOffset At);

public sealed record ServerSummaryDto(
    Guid ServerId,
    DateTimeOffset Time,
    double CpuPercent,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    double? DiskUsedPercent);

public sealed record LiveSampleDto(
    Guid ServerId,
    SystemMetricDto System,
    IReadOnlyList<LiveDiskDto> Disks,
    IReadOnlyList<LiveNetworkDto> Network);

public sealed record LiveDiskDto(string Mount, long UsedBytes, long TotalBytes);

public sealed record LiveNetworkDto(string Interface, double RxBytesPerSecond, double TxBytesPerSecond);
