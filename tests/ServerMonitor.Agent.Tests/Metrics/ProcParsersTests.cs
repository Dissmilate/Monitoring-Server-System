using ServerMonitor.Agent.Metrics;
using ServerMonitor.Agent.Metrics.Linux;

namespace ServerMonitor.Agent.Tests.Metrics;

public sealed class ProcParsersTests
{
    [Fact]
    public void Cpu_times_count_iowait_as_idle_and_exclude_guest_time()
    {
        const string stat = """
            cpu  4705 356 584 3699176 23060 0 277 0 100 0
            cpu0 1393 280 307 924486 5742 0 263 0 0 0
            intr 114930548 113199788 3 0 5
            """;

        var times = ProcParsers.ParseCpuTimes(stat);

        times.Idle.ShouldBe(3699176UL + 23060);
        times.Total.ShouldBe(4705UL + 356 + 584 + 3699176 + 23060 + 0 + 277 + 0);
    }

    [Fact]
    public void Busy_percent_is_the_non_idle_share_of_the_interval()
    {
        CpuTimes.BusyPercent(new CpuTimes(1000, 2000), new CpuTimes(1300, 2400)).ShouldBe(25);
        CpuTimes.BusyPercent(new CpuTimes(1000, 2000), new CpuTimes(1000, 2000)).ShouldBe(0);
    }

    [Fact]
    public void Meminfo_values_are_converted_from_kilobytes()
    {
        const string memInfo = """
            MemTotal:       16303428 kB
            MemFree:         1000000 kB
            MemAvailable:    8151714 kB
            Buffers:          200000 kB
            Cached:          3000000 kB
            SwapTotal:       2097148 kB
            SwapFree:        1048574 kB
            """;

        var info = ProcParsers.ParseMemInfo(memInfo);

        info.TotalBytes.ShouldBe(16303428L * 1024);
        info.AvailableBytes.ShouldBe(8151714L * 1024);
        info.SwapTotalBytes.ShouldBe(2097148L * 1024);
        info.SwapFreeBytes.ShouldBe(1048574L * 1024);
    }

    [Fact]
    public void Old_kernels_without_mem_available_fall_back_to_free_plus_caches()
    {
        const string memInfo = """
            MemTotal:       16000000 kB
            MemFree:         1000000 kB
            Buffers:          200000 kB
            Cached:          3000000 kB
            """;

        ProcParsers.ParseMemInfo(memInfo).AvailableBytes.ShouldBe(4200000L * 1024);
    }

    [Fact]
    public void Net_dev_yields_byte_counters_per_interface_without_loopback()
    {
        const string netDev = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
                lo:    1000      10    0    0    0     0          0         0     1000      10    0    0    0     0       0          0
              eth0:  123456     100    0    0    0     0          0         0   654321      90    0    0    0     0       0          0
            """;

        var counters = ProcParsers.ParseNetDev(netDev);

        counters.Keys.ShouldBe(["eth0"]);
        counters["eth0"].ShouldBe((123456L, 654321L));
    }

    [Fact]
    public void Process_start_time_survives_spaces_and_parentheses_in_the_command_name()
    {
        const string stat = "1 (my (init) proc) S 0 1 1 0 -1 4194560 1000 0 0 0 10 20 0 0 20 0 1 0 12345 1000000 100";

        ProcParsers.ParseProcessStartTicks(stat).ShouldBe(12345);
    }

    [Fact]
    public void Load_and_uptime_take_the_first_field()
    {
        ProcParsers.ParseLoadAverage1m("0.52 0.58 0.59 2/1203 12345").ShouldBe(0.52);
        ProcParsers.ParseUptimeSeconds("350735.47 234388.90").ShouldBe(350735.47);
    }

    [Fact]
    public void Cgroup_files_are_parsed_including_unlimited_values()
    {
        ProcParsers.ParseCgroupCpuUsageMicroseconds("usage_usec 5000000\nuser_usec 3000000\nsystem_usec 2000000").ShouldBe(5000000);
        ProcParsers.ParseCgroupMemoryStat("anon 1000\nactive_file 7\ninactive_file 2000\n", "inactive_file").ShouldBe(2000);
        ProcParsers.ParseCgroupMemoryStat("anon 1000\n", "inactive_file").ShouldBe(0);
        ProcParsers.ParseCgroupLimit("max\n").ShouldBeNull();
        ProcParsers.ParseCgroupLimit("1073741824\n").ShouldBe(1073741824);
    }
}
