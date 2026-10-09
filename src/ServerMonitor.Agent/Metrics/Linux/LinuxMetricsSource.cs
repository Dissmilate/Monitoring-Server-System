using Google.Protobuf.WellKnownTypes;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Metrics.Linux;

internal sealed class LinuxMetricsSource(
    string root,
    CgroupStats? cgroup,
    int cpuCount,
    TimeProvider time,
    Func<IReadOnlyList<DiskSample>> readDisks)
    : IMetricsSource
{
    // USER_HZ, 100 on all mainstream architectures.
    private const double ClockTicksPerSecond = 100;

    private readonly NetworkRateTracker _network = new();
    private CpuTimes? _previousCpu;
    private long? _previousCgroupUsage;
    private long _previousTimestamp;

    public string Description => cgroup is null ? "Linux host (/proc)" : $"Linux container (cgroup {cgroup.Version})";

    public static bool HasContainerMarker(string root) =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
        || File.Exists(Path.Combine(root, ".dockerenv"))
        || File.Exists(Path.Combine(root, "run/.containerenv"));

    public MetricSample Collect()
    {
        var timestamp = time.GetTimestamp();
        var elapsed = _previousTimestamp == 0 ? TimeSpan.Zero : time.GetElapsedTime(_previousTimestamp, timestamp);
        _previousTimestamp = timestamp;

        var memInfo = ProcParsers.ParseMemInfo(Read("proc/meminfo"));
        var hostUptime = ProcParsers.ParseUptimeSeconds(Read("proc/uptime"));
        var sample = new MetricSample { Time = Timestamp.FromDateTimeOffset(time.GetUtcNow()) };

        if (cgroup is not null)
        {
            sample.CpuPercent = CgroupCpuPercent(cgroup, elapsed);

            var memory = cgroup.ReadMemory();
            sample.MemoryUsedBytes = memory.UsedBytes;
            sample.MemoryTotalBytes = memory.LimitBytes is { } limit && limit < memInfo.TotalBytes ? limit : memInfo.TotalBytes;
            sample.SwapUsedBytes = memory.SwapUsedBytes;

            // Container uptime = host uptime - start time of PID 1.
            var pid1StartSeconds = ProcParsers.ParseProcessStartTicks(Read("proc/1/stat")) / ClockTicksPerSecond;
            sample.UptimeSeconds = (long)Math.Max(0, hostUptime - pid1StartSeconds);
        }
        else
        {
            var cpu = ProcParsers.ParseCpuTimes(Read("proc/stat"));
            sample.CpuPercent = _previousCpu is { } previous ? CpuTimes.BusyPercent(previous, cpu) : 0;
            _previousCpu = cpu;

            sample.MemoryUsedBytes = memInfo.TotalBytes - memInfo.AvailableBytes;
            sample.MemoryTotalBytes = memInfo.TotalBytes;
            sample.SwapUsedBytes = Math.Max(0, memInfo.SwapTotalBytes - memInfo.SwapFreeBytes);
            sample.UptimeSeconds = (long)hostUptime;
            sample.LoadAverage1M = ProcParsers.ParseLoadAverage1m(Read("proc/loadavg"));
        }

        // /proc/net/dev is per network namespace, so this is already the container's traffic.
        sample.Network.Add(_network.Update(ProcParsers.ParseNetDev(Read("proc/net/dev")), elapsed));
        sample.Disks.Add(readDisks());

        return sample;
    }

    private double CgroupCpuPercent(CgroupStats stats, TimeSpan elapsed)
    {
        var usage = stats.ReadCpuUsageMicroseconds();
        var previous = _previousCgroupUsage;
        _previousCgroupUsage = usage;

        if (previous is null || elapsed <= TimeSpan.Zero || usage < previous)
        {
            return 0;
        }

        var availableMicroseconds = elapsed.TotalMicroseconds * cpuCount;
        return Math.Clamp(100.0 * (usage - previous.Value) / availableMicroseconds, 0, 100);
    }

    private string Read(string relativePath) => File.ReadAllText(Path.Combine(root, relativePath));
}
