using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Logs;

// Position = offset + hash of the file head: a shorter file was truncated, a different head was rotated.
internal sealed class FileLogSource(string path, TimeProvider time) : ILogSource
{
    public const int MaxLineLength = 8 * 1024;

    public const int InitialTailBytes = 64 * 1024;

    private const int HeadLength = 256;
    private const int MaxReadBytes = 256 * 1024;

    public string Key => "file:" + path;

    public async Task<LogReadResult?> ReadAsync(string? position, int maxEntries, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        var length = stream.Length;

        long offset;
        var skipPartialLine = false;
        if (TryParsePosition(position, out var storedOffset, out var storedHead))
        {
            offset = length >= storedOffset && await HashHeadAsync(stream, storedOffset, ct) == storedHead ? storedOffset : 0;
        }
        else
        {
            offset = Math.Max(0, length - InitialTailBytes);
            skipPartialLine = offset > 0;
        }

        var entries = new List<LogEntry>();
        var buffer = new byte[(int)Math.Min(MaxReadBytes, length - offset)];
        stream.Position = offset;
        await stream.ReadExactlyAsync(buffer, ct);

        var consumed = 0;
        while (entries.Count < maxEntries && consumed < buffer.Length)
        {
            var newline = Array.IndexOf(buffer, (byte)'\n', consumed);
            if (newline < 0)
            {
                // Wait for the rest of the line, unless it alone fills the buffer.
                if (consumed > 0 || buffer.Length < MaxReadBytes)
                {
                    break;
                }

                newline = buffer.Length - 1;
            }

            if (skipPartialLine)
            {
                skipPartialLine = false;
            }
            else
            {
                var line = Encoding.UTF8.GetString(buffer, consumed, newline - consumed).TrimEnd('\r', '\n');
                if (line.Length > 0)
                {
                    entries.Add(CreateEntry(line, offset + consumed));
                }
            }

            consumed = newline + 1;
        }

        if (skipPartialLine)
        {
            return null;
        }

        var next = offset + consumed;
        var nextPosition = FormatPosition(next, await HashHeadAsync(stream, next, ct));
        return nextPosition == position ? null : new LogReadResult(entries, nextPosition);
    }

    private LogEntry CreateEntry(string line, long lineOffset)
    {
        var now = time.GetUtcNow();
        var timestamp = LogLineParser.ParseTimestamp(line) is { } parsed && parsed <= now.AddMinutes(5) ? parsed : now;

        return new LogEntry
        {
            Id = LogSources.EntryId(path, lineOffset.ToString(CultureInfo.InvariantCulture), line),
            Time = Timestamp.FromDateTimeOffset(timestamp),
            Level = LogLineParser.DetectLevel(line),
            Source = path,
            Message = line.Length <= MaxLineLength ? line : line[..MaxLineLength],
        };
    }

    private static async Task<string> HashHeadAsync(FileStream stream, long offset, CancellationToken ct)
    {
        var head = new byte[(int)Math.Min(HeadLength, offset)];
        stream.Position = 0;
        await stream.ReadExactlyAsync(head, ct);
        return Convert.ToHexStringLower(SHA256.HashData(head).AsSpan(0, 8));
    }

    private static string FormatPosition(long offset, string head) => string.Create(CultureInfo.InvariantCulture, $"{offset}:{head}");

    private static bool TryParsePosition(string? position, out long offset, out string head)
    {
        offset = 0;
        head = string.Empty;
        var separator = position?.IndexOf(':', StringComparison.Ordinal) ?? -1;
        if (separator <= 0 || !long.TryParse(position.AsSpan(0, separator), CultureInfo.InvariantCulture, out offset))
        {
            return false;
        }

        head = position![(separator + 1)..];
        return true;
    }
}
