using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Gateway.Services;

internal static class MetricBatchValidator
{
    public const int MaxSamplesPerBatch = 1000;
    public const int MaxSeriesPerSample = 32;
    public const int MaxNameLength = 255;

    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public static MetricBatch Sanitize(MetricBatch batch, DateTimeOffset now)
    {
        var result = new MetricBatch { BatchId = batch.BatchId };
        foreach (var sample in batch.Samples)
        {
            if (sample.Time is null)
            {
                continue;
            }

            var time = sample.Time.ToDateTimeOffset();
            if (time > now + MaxFutureSkew || time < now - MaxAge
                || !double.IsFinite(sample.CpuPercent)
                || sample.MemoryUsedBytes < 0 || sample.MemoryTotalBytes < 0 || sample.SwapUsedBytes < 0)
            {
                continue;
            }

            var clean = sample.Clone();
            clean.CpuPercent = Math.Clamp(sample.CpuPercent, 0, 100);
            if (clean.HasLoadAverage1M && !double.IsFinite(clean.LoadAverage1M))
            {
                clean.ClearLoadAverage1M();
            }

            clean.Disks.Clear();
            clean.Disks.Add(sample.Disks
                .Where(d => IsValidName(d.Mount) && d.UsedBytes >= 0 && d.TotalBytes >= 0)
                .Take(MaxSeriesPerSample));

            clean.Network.Clear();
            clean.Network.Add(sample.Network
                .Where(n => IsValidName(n.Interface) && IsValidRate(n.RxBytesPerSecond) && IsValidRate(n.TxBytesPerSecond))
                .Take(MaxSeriesPerSample));

            result.Samples.Add(clean);
        }

        return result;
    }

    private static bool IsValidName(string name) => name.Length is > 0 and <= MaxNameLength;

    private static bool IsValidRate(double rate) => double.IsFinite(rate) && rate >= 0;
}
