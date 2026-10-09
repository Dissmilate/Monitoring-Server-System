using System.Globalization;

namespace ServerMonitor.Dashboard.Services;

public static class Format
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(double bytes)
    {
        var unit = 0;
        while (Math.Abs(bytes) >= 1024 && unit < ByteUnits.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{bytes:0.#} {ByteUnits[unit]}");
    }

    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Percent(double value) => string.Create(CultureInfo.InvariantCulture, $"{value:0.#}%");

    public static string Uptime(long seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalDays >= 1
            ? $"{(int)span.TotalDays}d {span.Hours}h"
            : span.TotalHours >= 1 ? $"{span.Hours}h {span.Minutes}m" : $"{span.Minutes}m {span.Seconds}s";
    }

    public static string Ago(DateTimeOffset? time, DateTimeOffset now)
    {
        if (time is null)
        {
            return "never";
        }

        var age = now - time.Value;
        return age.TotalSeconds < 60 ? "just now"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalHours < 24 ? $"{(int)age.TotalHours} h ago"
            : $"{(int)age.TotalDays} d ago";
    }

    public static string LocalTime(DateTimeOffset time) =>
        time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
