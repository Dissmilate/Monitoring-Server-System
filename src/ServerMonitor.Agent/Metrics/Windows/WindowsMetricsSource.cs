using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Google.Protobuf.WellKnownTypes;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Metrics.Windows;

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsMetricsSource(TimeProvider time, Func<IReadOnlyList<DiskSample>> readDisks) : IMetricsSource
{
    private readonly NetworkRateTracker _network = new();
    private CpuTimes? _previousCpu;
    private long _previousTimestamp;

    public string Description => "Windows host (kernel32)";

    public MetricSample Collect()
    {
        var timestamp = time.GetTimestamp();
        var elapsed = _previousTimestamp == 0 ? TimeSpan.Zero : time.GetElapsedTime(_previousTimestamp, timestamp);
        _previousTimestamp = timestamp;

        // Kernel time includes idle time.
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var cpu = new CpuTimes(idle, kernel + user);
        var cpuPercent = _previousCpu is { } previous ? CpuTimes.BusyPercent(previous, cpu) : 0;
        _previousCpu = cpu;

        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref memory))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var physicalUsed = (long)(memory.TotalPhys - memory.AvailPhys);

        // Commit charge beyond physical memory ~ page file usage.
        var commitUsed = (long)(memory.TotalPageFile - memory.AvailPageFile);

        var sample = new MetricSample
        {
            Time = Timestamp.FromDateTimeOffset(time.GetUtcNow()),
            CpuPercent = cpuPercent,
            MemoryUsedBytes = physicalUsed,
            MemoryTotalBytes = (long)memory.TotalPhys,
            SwapUsedBytes = Math.Max(0, commitUsed - physicalUsed),
            UptimeSeconds = Environment.TickCount64 / 1000,
        };

        sample.Network.Add(_network.Update(ReadNetworkCounters(), elapsed));
        sample.Disks.Add(readDisks());
        return sample;
    }

    private static Dictionary<string, (long Rx, long Tx)> ReadNetworkCounters()
    {
        var counters = new Dictionary<string, (long Rx, long Tx)>(StringComparer.Ordinal);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            var stats = nic.GetIPStatistics();
            counters[nic.Name] = (stats.BytesReceived, stats.BytesSent);
        }

        return counters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
