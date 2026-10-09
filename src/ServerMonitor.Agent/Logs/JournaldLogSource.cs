using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Logs;

internal sealed class JournaldLogSource : ILogSource
{
    public const int InitialEntries = 100;

    public string Key => "journald";

    public static bool IsAvailable() =>
        Directory.Exists("/run/systemd/journal") && (File.Exists("/usr/bin/journalctl") || File.Exists("/bin/journalctl"));

    public async Task<LogReadResult?> ReadAsync(string? position, int maxEntries, CancellationToken ct)
    {
        var start = new ProcessStartInfo("journalctl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in (string[])["--output=json", "--no-pager", "--quiet"])
        {
            start.ArgumentList.Add(argument);
        }

        if (position is null)
        {
            start.ArgumentList.Add($"--lines={InitialEntries}");
        }
        else
        {
            start.ArgumentList.Add($"--after-cursor={position}");
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("journalctl did not start.");
        var entries = new List<LogEntry>();
        var cursor = position;
        try
        {
            while (entries.Count < maxEntries && await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (JournalEntryParser.TryParse(line, out var entry, out var entryCursor))
                {
                    entries.Add(entry);
                    cursor = entryCursor;
                }
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }

        return cursor is null || cursor == position ? null : new LogReadResult(entries, cursor);
    }
}

internal static class JournalEntryParser
{
    public static bool TryParse(string json, [NotNullWhen(true)] out LogEntry? entry, [NotNullWhen(true)] out string? cursor)
    {
        entry = null;
        cursor = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("__CURSOR", out var cursorElement)
                || !TryGetString(root, "__REALTIME_TIMESTAMP", out var realtime)
                || !long.TryParse(realtime, CultureInfo.InvariantCulture, out var microseconds)
                || !TryGetMessage(root, out var message))
            {
                return false;
            }

            cursor = cursorElement.GetString()!;
            var source = TryGetString(root, "_SYSTEMD_UNIT", out var unit) ? unit
                : TryGetString(root, "SYSLOG_IDENTIFIER", out var identifier) ? identifier
                : "journald";

            entry = new LogEntry
            {
                Id = LogSources.EntryId("journald", cursor),
                Time = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddTicks(microseconds * 10)),
                Level = TryGetString(root, "PRIORITY", out var priority) ? FromPriority(priority) : LogSeverity.Information,
                Source = source,
                Message = message.Length <= FileLogSource.MaxLineLength ? message : message[..FileLogSource.MaxLineLength],
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static LogSeverity FromPriority(string priority) => priority switch
    {
        "0" or "1" or "2" => LogSeverity.Critical,
        "3" => LogSeverity.Error,
        "4" => LogSeverity.Warning,
        "7" => LogSeverity.Debug,
        _ => LogSeverity.Information,
    };

    private static bool TryGetString(JsonElement root, string name, [NotNullWhen(true)] out string? value)
    {
        value = root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return value is not null;
    }

    // journalctl emits non-UTF-8 messages as byte arrays.
    private static bool TryGetMessage(JsonElement root, [NotNullWhen(true)] out string? message)
    {
        message = null;
        if (!root.TryGetProperty("MESSAGE", out var element))
        {
            return false;
        }

        message = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Array => Encoding.UTF8.GetString([.. element.EnumerateArray().Select(b => (byte)b.GetInt32())]),
            _ => null,
        };
        return message is not null;
    }
}
