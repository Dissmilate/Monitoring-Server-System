using ServerMonitor.Application.Abstractions;
using ServerMonitor.Contracts.Metrics;
using ServerMonitor.Domain.Common;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Metrics;

public sealed record GetServerMetricsQuery(
    Guid ServerId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Resolution);

public static class MetricErrors
{
    public static readonly Error InvalidRange =
        Error.Validation("Metrics.InvalidRange", "'from' must be earlier than 'to' and the range must not exceed 366 days.");

    public static readonly Error InvalidResolution =
        Error.Validation("Metrics.InvalidResolution", "Resolution must be one of: auto, raw, 1m, 1h.");
}

public sealed class GetServerMetricsHandler(IServerRepository servers, IMetricReader reader, TimeProvider time)
{
    public static readonly TimeSpan DefaultRange = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);

    public async Task<Result<MetricsDto>> HandleAsync(GetServerMetricsQuery query, CancellationToken ct)
    {
        var to = query.To ?? time.GetUtcNow();
        var from = query.From ?? to - DefaultRange;
        if (from >= to || to - from > MaxRange)
        {
            return MetricErrors.InvalidRange;
        }

        if (!TryParseResolution(query.Resolution, to - from, out var resolution))
        {
            return MetricErrors.InvalidResolution;
        }

        if (!await servers.ExistsAsync(query.ServerId, ct))
        {
            return ServerErrors.NotFound(query.ServerId);
        }

        return await reader.ReadAsync(query.ServerId, from, to, resolution, ct);
    }

    public static MetricResolution ChooseResolution(TimeSpan range) => range switch
    {
        _ when range <= TimeSpan.FromHours(3) => MetricResolution.Raw,
        _ when range <= TimeSpan.FromDays(3) => MetricResolution.Minute,
        _ => MetricResolution.Hour,
    };

    private static bool TryParseResolution(string? value, TimeSpan range, out MetricResolution resolution)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "auto":
                resolution = ChooseResolution(range);
                return true;
            case "raw":
                resolution = MetricResolution.Raw;
                return true;
            case "1m":
                resolution = MetricResolution.Minute;
                return true;
            case "1h":
                resolution = MetricResolution.Hour;
                return true;
            default:
                resolution = default;
                return false;
        }
    }
}
