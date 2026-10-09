using System.Security.Cryptography;
using System.Text;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Logs;

internal sealed class LogOptions
{
    public string[] Files { get; set; } = [];

    public bool? Journald { get; set; }

    public string[]? EventLogChannels { get; set; }

    public int BufferCapacity { get; set; } = 100_000;
}

internal sealed record LogReadResult(IReadOnlyList<LogEntry> Entries, string Position);

internal interface ILogSource
{
    string Key { get; }

    Task<LogReadResult?> ReadAsync(string? position, int maxEntries, CancellationToken ct);
}

internal static class LogSources
{
    public static IReadOnlyList<ILogSource> Create(LogOptions options, TimeProvider time)
    {
        var sources = new List<ILogSource>();
        sources.AddRange(options.Files.Distinct(StringComparer.Ordinal).Select(path => new FileLogSource(path, time)));

        if (OperatingSystem.IsLinux() && (options.Journald ?? JournaldLogSource.IsAvailable()))
        {
            sources.Add(new JournaldLogSource());
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var channel in (options.EventLogChannels ?? ["Application", "System"]).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                sources.Add(new WindowsEventLogSource(channel));
            }
        }

        return sources;
    }

    // Same line -> same id, so re-reads are deduplicated on the server.
    public static string EntryId(params ReadOnlySpan<string> parts)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts.ToArray())));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
