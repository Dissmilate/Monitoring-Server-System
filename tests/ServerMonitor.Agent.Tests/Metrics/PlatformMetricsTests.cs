using System.Runtime.Versioning;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.Agent.Metrics;
using ServerMonitor.Agent.Metrics.Windows;

namespace ServerMonitor.Agent.Tests.Metrics;

public sealed class PlatformMetricsTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Windows_source_reads_plausible_values_from_the_kernel()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows APIs.");
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = new WindowsMetricsSource(time, DiskUsage.Collect);

        source.Collect();
        time.Advance(TimeSpan.FromSeconds(1));
        var sample = source.Collect();

        sample.CpuPercent.ShouldBeInRange(0, 100);
        sample.MemoryTotalBytes.ShouldBeGreaterThan(0);
        sample.MemoryUsedBytes.ShouldBeInRange(1, sample.MemoryTotalBytes);
        sample.UptimeSeconds.ShouldBeGreaterThan(0);
        sample.HasLoadAverage1M.ShouldBeFalse();
        sample.Disks.ShouldContain(d => d.TotalBytes > 0 && d.UsedBytes <= d.TotalBytes);
    }

    [Theory]
    [InlineData(DriveType.Fixed, "ext4", true)]
    [InlineData(DriveType.Fixed, "NTFS", true)]
    [InlineData(DriveType.Unknown, "overlay", true)]
    [InlineData(DriveType.Ram, "tmpfs", false)]
    [InlineData(DriveType.Unknown, "proc", false)]
    [InlineData(DriveType.Unknown, "cgroup2", false)]
    [InlineData(DriveType.Network, "nfs4", false)]
    [InlineData(DriveType.CDRom, "iso9660", false)]
    public void Only_local_persistent_filesystems_are_reported(DriveType type, string format, bool expected) =>
        DiskUsage.IsRelevant(type, format, Path.GetTempPath()).ShouldBe(expected);
}
