using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Gateway.Services;

internal static class LogBatchValidator
{
    public const int MaxEntriesPerBatch = 1000;
    public const int MaxBatchBytes = 768 * 1024;
    public const int MaxIdLength = 128;
    public const int MaxSourceLength = 255;
    public const int MaxMessageLength = 8 * 1024;

    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    public static LogBatch Sanitize(LogBatch batch, DateTimeOffset now)
    {
        var result = new LogBatch { BatchId = batch.BatchId };
        foreach (var entry in batch.Entries)
        {
            if (entry.Time is null || entry.Id.Length is 0 or > MaxIdLength)
            {
                continue;
            }

            var time = entry.Time.ToDateTimeOffset();
            if (time > now + MaxFutureSkew || time < now - MaxAge)
            {
                continue;
            }

            result.Entries.Add(new LogEntry
            {
                Id = entry.Id,
                Time = entry.Time,
                Level = Enum.IsDefined(entry.Level) && entry.Level != LogSeverity.Unspecified ? entry.Level : LogSeverity.Information,
                Source = Truncate(entry.Source.Length == 0 ? "unknown" : entry.Source, MaxSourceLength),
                Message = Truncate(entry.Message, MaxMessageLength),
            });
        }

        return result;
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
