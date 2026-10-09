using ServerMonitor.Contracts.Live;
using ServerMonitor.Contracts.Metrics;

namespace ServerMonitor.Application.Metrics;

public enum MetricResolution
{
    Raw,
    Minute,
    Hour,
}

public interface IMetricWriter
{
    Task WriteAsync(MetricWriteBatch batch, CancellationToken ct);
}

public interface IMetricReader
{
    Task<MetricsDto> ReadAsync(
        Guid serverId,
        DateTimeOffset from,
        DateTimeOffset to,
        MetricResolution resolution,
        CancellationToken ct);

    Task<IReadOnlyList<ServerSummaryDto>> ReadLatestAsync(DateTimeOffset since, CancellationToken ct);
}

public sealed class GetLatestMetricsHandler(IMetricReader reader, TimeProvider time)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    public Task<IReadOnlyList<ServerSummaryDto>> HandleAsync(CancellationToken ct) =>
        reader.ReadLatestAsync(time.GetUtcNow() - MaxAge, ct);
}
