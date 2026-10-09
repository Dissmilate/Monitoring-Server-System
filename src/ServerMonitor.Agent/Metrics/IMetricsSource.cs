using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Metrics;

internal interface IMetricsSource
{
    string Description { get; }

    MetricSample Collect();
}

internal readonly record struct CpuTimes(ulong Idle, ulong Total)
{
    public static double BusyPercent(CpuTimes previous, CpuTimes current)
    {
        var total = current.Total - previous.Total;
        var idle = current.Idle - previous.Idle;
        return current.Total <= previous.Total || idle > total ? 0 : Math.Clamp(100.0 * (total - idle) / total, 0, 100);
    }
}

internal sealed class NetworkRateTracker
{
    private Dictionary<string, (long Rx, long Tx)> _previous = new(StringComparer.Ordinal);

    public IReadOnlyList<NetworkSample> Update(IReadOnlyDictionary<string, (long Rx, long Tx)> counters, TimeSpan elapsed)
    {
        var samples = new List<NetworkSample>(counters.Count);
        foreach (var (name, current) in counters)
        {
            samples.Add(new NetworkSample
            {
                Interface = name,
                RxBytesPerSecond = Rate(_previous.GetValueOrDefault(name, current).Rx, current.Rx, elapsed),
                TxBytesPerSecond = Rate(_previous.GetValueOrDefault(name, current).Tx, current.Tx, elapsed),
            });
        }

        _previous = new Dictionary<string, (long Rx, long Tx)>(counters, StringComparer.Ordinal);
        return samples;
    }

    // Counter was reset (interface re-created).
    private static double Rate(long previous, long current, TimeSpan elapsed) =>
        elapsed <= TimeSpan.Zero || current < previous ? 0 : (current - previous) / elapsed.TotalSeconds;
}
