using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using Google.Protobuf.WellKnownTypes;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Logs;

[SupportedOSPlatform("windows")]
internal sealed class WindowsEventLogSource(string channel) : ILogSource
{
    public const int InitialEvents = 100;

    public string Key => "eventlog:" + channel;

    public Task<LogReadResult?> ReadAsync(string? position, int maxEntries, CancellationToken ct) =>
        Task.Run(() => Read(position, maxEntries), ct);

    private LogReadResult? Read(string? position, int maxEntries)
    {
        var last = long.TryParse(position, CultureInfo.InvariantCulture, out var stored)
            ? stored
            : Math.Max(0, LatestRecordId() - InitialEvents);

        var entries = new List<LogEntry>();
        var query = new EventLogQuery(channel, PathType.LogName, $"*[System[(EventRecordID > {last})]]");
        using (var reader = new EventLogReader(query))
        {
            while (entries.Count < maxEntries && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    last = record.RecordId ?? last;
                    entries.Add(ToEntry(record));
                }
            }
        }

        // The log was cleared: record ids start from 1 again.
        if (entries.Count == 0 && LatestRecordId() < last)
        {
            last = 0;
        }

        var next = last.ToString(CultureInfo.InvariantCulture);
        return next == position ? null : new LogReadResult(entries, next);
    }

    private LogEntry ToEntry(EventRecord record)
    {
        string? message;
        try
        {
            message = record.FormatDescription();
        }
        catch (EventLogException)
        {
            message = null;
        }

        message ??= string.Join(", ", record.Properties.Select(p => p.Value));

        return new LogEntry
        {
            Id = LogSources.EntryId("eventlog", channel, (record.RecordId ?? 0).ToString(CultureInfo.InvariantCulture)),
            Time = Timestamp.FromDateTimeOffset(record.TimeCreated is { } created ? new DateTimeOffset(created) : DateTimeOffset.UtcNow),
            Level = record.Level switch
            {
                1 => LogSeverity.Critical,
                2 => LogSeverity.Error,
                3 => LogSeverity.Warning,
                5 => LogSeverity.Debug,
                _ => LogSeverity.Information,
            },
            Source = $"{channel}/{record.ProviderName}",
            Message = message.Length <= FileLogSource.MaxLineLength ? message : message[..FileLogSource.MaxLineLength],
        };
    }

    private long LatestRecordId()
    {
        using var reader = new EventLogReader(new EventLogQuery(channel, PathType.LogName) { ReverseDirection = true });
        using var record = reader.ReadEvent();
        return record?.RecordId ?? 0;
    }
}
