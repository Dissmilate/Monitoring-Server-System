using ServerMonitor.Agent.Metrics.Linux;
using ServerMonitor.Agent.Metrics.Windows;

namespace ServerMonitor.Agent.Metrics;

internal static class MetricsSources
{
    public static IMetricsSource ForCurrentPlatform(AgentOptions options, TimeProvider time)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsMetricsSource(time, DiskUsage.Collect);
        }

        if (OperatingSystem.IsLinux())
        {
            const string root = "/";
            var cgroup = options.UseCgroupMetrics switch
            {
                false => null,
                true => CgroupStats.Detect(root),
                null => LinuxMetricsSource.HasContainerMarker(root) ? CgroupStats.Detect(root) : null,
            };

            return new LinuxMetricsSource(root, cgroup, Environment.ProcessorCount, time, DiskUsage.Collect);
        }

        throw new PlatformNotSupportedException("The agent supports Linux and Windows.");
    }
}
