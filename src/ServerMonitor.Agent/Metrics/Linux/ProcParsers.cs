using System.Globalization;

namespace ServerMonitor.Agent.Metrics.Linux;

internal readonly record struct MemInfo(long TotalBytes, long AvailableBytes, long SwapTotalBytes, long SwapFreeBytes);

internal static class ProcParsers
{
    public static CpuTimes ParseCpuTimes(string procStat)
    {
        var line = FirstLine(procStat, "cpu ");
        var fields = Fields(line).Skip(1).Take(8).Select(ParseULong).ToArray();
        if (fields.Length < 4)
        {
            throw new FormatException("Unexpected /proc/stat format.");
        }

        var idle = fields[3] + (fields.Length > 4 ? fields[4] : 0);
        var total = fields.Aggregate(0UL, (sum, value) => sum + value);
        return new CpuTimes(idle, total);
    }

    public static MemInfo ParseMemInfo(string memInfo)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in Lines(memInfo))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                values[line[..colon]] = ParseLong(Fields(line[(colon + 1)..])[0]) * 1024;
            }
        }

        var total = values["MemTotal"];

        var available = values.TryGetValue("MemAvailable", out var a)
            ? a
            : values.GetValueOrDefault("MemFree") + values.GetValueOrDefault("Buffers") + values.GetValueOrDefault("Cached");

        return new MemInfo(total, available, values.GetValueOrDefault("SwapTotal"), values.GetValueOrDefault("SwapFree"));
    }

    public static double ParseLoadAverage1m(string loadAvg) => ParseDouble(Fields(loadAvg)[0]);

    public static double ParseUptimeSeconds(string uptime) => ParseDouble(Fields(uptime)[0]);

    public static Dictionary<string, (long Rx, long Tx)> ParseNetDev(string netDev)
    {
        var counters = new Dictionary<string, (long Rx, long Tx)>(StringComparer.Ordinal);
        foreach (var line in Lines(netDev).Skip(2))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var fields = Fields(line[(colon + 1)..]);
            if (name == "lo" || fields.Length < 9)
            {
                continue;
            }

            counters[name] = (ParseLong(fields[0]), ParseLong(fields[8]));
        }

        return counters;
    }

    public static long ParseProcessStartTicks(string procPidStat)
    {
        var afterName = procPidStat[(procPidStat.LastIndexOf(')') + 1)..];
        return ParseLong(Fields(afterName)[19]); // fields 3.. follow the name; 22 - 3 = 19
    }

    public static long ParseCgroupCpuUsageMicroseconds(string cpuStat) => ParseKeyValue(cpuStat, "usage_usec");

    public static long ParseCgroupMemoryStat(string memoryStat, string key) =>
        Lines(memoryStat).Any(l => l.StartsWith(key + " ", StringComparison.Ordinal)) ? ParseKeyValue(memoryStat, key) : 0;

    public static long? ParseCgroupLimit(string content)
    {
        var value = content.Trim();
        return value == "max" ? null : ParseLong(value);
    }

    public static long ParseCgroupV1Value(string content) => ParseLong(content.Trim());

    public static long? ParseCgroupV1Limit(string content)
    {
        var value = ParseCgroupV1Value(content);
        return value >= 1L << 62 ? null : value;
    }

    private static long ParseKeyValue(string content, string key)
    {
        var line = FirstLine(content, key + " ");
        return ParseLong(Fields(line)[1]);
    }

    private static string FirstLine(string content, string prefix) =>
        Lines(content).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))
        ?? throw new FormatException($"Line starting with '{prefix.Trim()}' not found.");

    private static IEnumerable<string> Lines(string content) =>
        content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] Fields(string line) =>
        line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static long ParseLong(string value) => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static ulong ParseULong(string value) => ulong.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static double ParseDouble(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
}
