using Microsoft.Extensions.Time.Testing;
using ServerMonitor.Agent.Metrics.Linux;

namespace ServerMonitor.Agent.Tests.Metrics;

public sealed class LinuxMetricsSourceTests : IDisposable
{
    private const long MiB = 1024 * 1024;

    private readonly TempDirectory _root = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

    public LinuxMetricsSourceTests()
    {
        _root.Write("proc/meminfo", """
            MemTotal:        8192000 kB
            MemAvailable:    6144000 kB
            SwapTotal:       1024000 kB
            SwapFree:         768000 kB
            """);
        _root.Write("proc/uptime", "3600.50 7000.00");
        _root.Write("proc/loadavg", "0.75 0.50 0.25 1/100 123");
        _root.Write("proc/1/stat", "1 (dotnet) S 0 1 1 0 -1 4194560 1000 0 0 0 10 20 0 0 20 0 1 0 300000 1000000 100");
        WriteCpu(idle: 1000, busy: 1000);
        WriteNetwork(rx: 1000, tx: 2000);
    }

    [Fact]
    public void Host_mode_reports_rates_over_the_interval_between_samples()
    {
        var source = Source(cgroup: null);

        var first = source.Collect();
        first.CpuPercent.ShouldBe(0);
        first.Network.Single().RxBytesPerSecond.ShouldBe(0);

        WriteCpu(idle: 1300, busy: 1100);
        WriteNetwork(rx: 6000, tx: 12000);
        _time.Advance(TimeSpan.FromSeconds(10));

        var second = source.Collect();

        second.Time.ToDateTimeOffset().ShouldBe(_time.GetUtcNow());
        second.CpuPercent.ShouldBe(25);
        second.Network.Single().Interface.ShouldBe("eth0");
        second.Network.Single().RxBytesPerSecond.ShouldBe(500);
        second.Network.Single().TxBytesPerSecond.ShouldBe(1000);
    }

    [Fact]
    public void Host_mode_reports_memory_swap_uptime_and_load()
    {
        var sample = Source(cgroup: null).Collect();

        sample.MemoryTotalBytes.ShouldBe(8192000L * 1024);
        sample.MemoryUsedBytes.ShouldBe((8192000L - 6144000) * 1024);
        sample.SwapUsedBytes.ShouldBe((1024000L - 768000) * 1024);
        sample.UptimeSeconds.ShouldBe(3600);
        sample.LoadAverage1M.ShouldBe(0.75);
    }

    [Fact]
    public void Cgroup_v2_measures_the_container_instead_of_the_host()
    {
        WriteCgroupV2(usageMicroseconds: 1_000_000, memoryMax: "1073741824");
        var source = Source(CgroupStats.Detect(_root.Path).ShouldBeOfType<CgroupV2Stats>());

        var first = source.Collect();

        first.MemoryUsedBytes.ShouldBe(500 * MiB);
        first.MemoryTotalBytes.ShouldBe(1024 * MiB);
        first.SwapUsedBytes.ShouldBe(16 * MiB);
        first.UptimeSeconds.ShouldBe(600);
        first.HasLoadAverage1M.ShouldBeFalse();

        WriteCgroupV2(usageMicroseconds: 11_000_000, memoryMax: "max");
        _time.Advance(TimeSpan.FromSeconds(10));

        var second = source.Collect();

        second.CpuPercent.ShouldBe(50);
        second.MemoryTotalBytes.ShouldBe(8192000L * 1024);
    }

    [Fact]
    public void Cgroup_v1_measures_the_container_instead_of_the_host()
    {
        WriteCgroupV1(usageNanoseconds: 2_000_000_000, memoryLimit: 512 * MiB);
        var source = Source(CgroupStats.Detect(_root.Path).ShouldBeOfType<CgroupV1Stats>());

        var first = source.Collect();

        first.MemoryUsedBytes.ShouldBe(300 * MiB);
        first.MemoryTotalBytes.ShouldBe(512 * MiB);
        first.SwapUsedBytes.ShouldBe(0);
        first.UptimeSeconds.ShouldBe(600);

        WriteCgroupV1(usageNanoseconds: 7_000_000_000, memoryLimit: 9223372036854771712);
        _time.Advance(TimeSpan.FromSeconds(10));

        var second = source.Collect();

        second.CpuPercent.ShouldBe(25);
        second.MemoryTotalBytes.ShouldBe(8192000L * 1024);
    }

    [Fact]
    public void Cgroup_hierarchy_is_detected_from_the_files_present()
    {
        CgroupStats.Detect(_root.Path).ShouldBeNull();

        WriteCgroupV1(usageNanoseconds: 0, memoryLimit: 512 * MiB);
        CgroupStats.Detect(_root.Path).ShouldBeOfType<CgroupV1Stats>();

        WriteCgroupV2(usageMicroseconds: 0, memoryMax: "max");
        CgroupStats.Detect(_root.Path).ShouldBeOfType<CgroupV2Stats>();
    }

    [Fact]
    public void Container_marker_file_is_recognised()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true",
            "The environment variable already marks this process as containerized.");

        LinuxMetricsSource.HasContainerMarker(_root.Path).ShouldBeFalse();

        _root.Write(".dockerenv", string.Empty);
        LinuxMetricsSource.HasContainerMarker(_root.Path).ShouldBeTrue();
    }

    public void Dispose() => _root.Dispose();

    private LinuxMetricsSource Source(CgroupStats? cgroup) => new(_root.Path, cgroup, cpuCount: 2, _time, () => []);

    private void WriteCpu(long idle, long busy) =>
        _root.Write("proc/stat", $"cpu  {busy} 0 0 {idle} 0 0 0 0 0 0\n");

    private void WriteNetwork(long rx, long tx) => _root.Write("proc/net/dev", $"""
        Inter-|   Receive
         face |bytes    packets errs drop fifo frame compressed multicast|bytes
            lo: 5 1 0 0 0 0 0 0 5 1 0 0 0 0 0 0
          eth0: {rx} 1 0 0 0 0 0 0 {tx} 1 0 0 0 0 0 0
        """);

    private void WriteCgroupV2(long usageMicroseconds, string memoryMax)
    {
        _root.Write("sys/fs/cgroup/cgroup.controllers", "cpuset cpu io memory pids\n");
        _root.Write("sys/fs/cgroup/cpu.stat", $"usage_usec {usageMicroseconds}\nuser_usec 0\nsystem_usec 0\n");
        _root.Write("sys/fs/cgroup/memory.current", $"{600 * MiB}\n");
        _root.Write("sys/fs/cgroup/memory.stat", $"anon {400 * MiB}\ninactive_file {100 * MiB}\n");
        _root.Write("sys/fs/cgroup/memory.max", memoryMax + "\n");
        _root.Write("sys/fs/cgroup/memory.swap.current", $"{16 * MiB}\n");
    }

    private void WriteCgroupV1(long usageNanoseconds, long memoryLimit)
    {
        _root.Write("sys/fs/cgroup/cpuacct/cpuacct.usage", $"{usageNanoseconds}\n");
        _root.Write("sys/fs/cgroup/memory/memory.usage_in_bytes", $"{400 * MiB}\n");
        _root.Write("sys/fs/cgroup/memory/memory.stat", $"cache {150 * MiB}\ninactive_file 1\ntotal_inactive_file {100 * MiB}\n");
        _root.Write("sys/fs/cgroup/memory/memory.limit_in_bytes", $"{memoryLimit}\n");
    }
}
