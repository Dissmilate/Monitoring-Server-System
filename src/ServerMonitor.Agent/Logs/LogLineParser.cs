using System.Globalization;
using System.Text.RegularExpressions;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Logs;

internal static partial class LogLineParser
{
    private const int LevelSearchLength = 200;

    public static LogSeverity DetectLevel(string line)
    {
        var match = LevelPattern().Match(line.Length <= LevelSearchLength ? line : line[..LevelSearchLength]);
        if (!match.Success)
        {
            return LogSeverity.Information;
        }

        return match.Value.ToUpperInvariant() switch
        {
            "TRACE" or "TRC" => LogSeverity.Trace,
            "DEBUG" or "DBG" => LogSeverity.Debug,
            "WARN" or "WARNING" or "WRN" => LogSeverity.Warning,
            "ERROR" or "ERR" or "FAIL" => LogSeverity.Error,
            "FATAL" or "CRITICAL" or "CRIT" or "CRT" => LogSeverity.Critical,
            _ => LogSeverity.Information,
        };
    }

    public static DateTimeOffset? ParseTimestamp(string line)
    {
        var match = TimestampPattern().Match(line);
        return match.Success && DateTimeOffset.TryParse(
            match.Groups[1].Value.Replace(',', '.'),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var time)
            ? time
            : null;
    }

    [GeneratedRegex(
        @"\b(TRACE|TRC|DEBUG|DBG|INFO|INFORMATION|INF|NOTICE|WARN|WARNING|WRN|ERROR|ERR|FAIL|FATAL|CRITICAL|CRIT|CRT)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelPattern();

    [GeneratedRegex(@"^\[?(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,7})?(?:Z|[+-]\d{2}:?\d{2})?)", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();
}
