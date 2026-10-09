using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using ServerMonitor.Contracts.Logs;
using ServerMonitor.Domain.Common;

namespace ServerMonitor.Application.Logs;

public sealed record LogRecord(string Id, Guid ServerId, DateTimeOffset Time, string Level, string Source, string Message);

public sealed record LogQuery(
    Guid? ServerId,
    IReadOnlyList<string> Levels,
    string? Text,
    DateTimeOffset From,
    DateTimeOffset To,
    int Limit,
    LogCursor? After);

public sealed record LogCursor(long TimeMilliseconds, string EntryId)
{
    public string Encode() =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{TimeMilliseconds}|{EntryId}")));

    public static bool TryDecode(string? value, [NotNullWhen(true)] out LogCursor? cursor)
    {
        cursor = null;
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value ?? string.Empty));
            var separator = text.IndexOf('|', StringComparison.Ordinal);
            if (separator > 0 && long.TryParse(text.AsSpan(0, separator), CultureInfo.InvariantCulture, out var time))
            {
                cursor = new LogCursor(time, text[(separator + 1)..]);
            }
        }
        catch (FormatException)
        {
        }

        return cursor is not null;
    }
}

public interface ILogWriter
{
    Task WriteAsync(IReadOnlyList<LogRecord> records, CancellationToken ct);
}

public interface ILogReader
{
    Task<LogSearchResultDto> SearchAsync(LogQuery query, CancellationToken ct);
}

public sealed class LogStoreException(string message) : Exception(message);

public sealed record SearchLogsRequest(
    Guid? ServerId,
    string? Levels,
    string? Text,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int? Limit,
    string? Cursor);

public static class LogErrors
{
    public static readonly Error InvalidRange =
        Error.Validation("Logs.InvalidRange", "'from' must be earlier than 'to'.");

    public static readonly Error InvalidLevel =
        Error.Validation("Logs.InvalidLevel", $"Levels must be a comma-separated list of: {string.Join(", ", LogLevels.All)}.");

    public static readonly Error InvalidCursor =
        Error.Validation("Logs.InvalidCursor", "The cursor is not valid; start the search again without it.");

    public static readonly Error TextTooLong =
        Error.Validation("Logs.TextTooLong", "The search text must be at most 200 characters.");
}

public sealed class SearchLogsHandler(ILogReader reader, TimeProvider time)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public static readonly TimeSpan DefaultRange = TimeSpan.FromHours(24);

    public async Task<Result<LogSearchResultDto>> HandleAsync(SearchLogsRequest request, CancellationToken ct)
    {
        var to = request.To ?? time.GetUtcNow();
        var from = request.From ?? to - DefaultRange;
        if (from >= to)
        {
            return LogErrors.InvalidRange;
        }

        var levels = (request.Levels ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalized = new List<string>();
        foreach (var level in levels)
        {
            var match = LogLevels.All.FirstOrDefault(l => string.Equals(l, level, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return LogErrors.InvalidLevel;
            }

            normalized.Add(match);
        }

        if (request.Text is { Length: > 200 })
        {
            return LogErrors.TextTooLong;
        }

        LogCursor? after = null;
        if (!string.IsNullOrEmpty(request.Cursor) && !LogCursor.TryDecode(request.Cursor, out after))
        {
            return LogErrors.InvalidCursor;
        }

        var query = new LogQuery(
            request.ServerId,
            normalized,
            string.IsNullOrWhiteSpace(request.Text) ? null : request.Text.Trim(),
            from,
            to,
            Math.Clamp(request.Limit ?? DefaultLimit, 1, MaxLimit),
            after);

        return await reader.SearchAsync(query, ct);
    }
}
