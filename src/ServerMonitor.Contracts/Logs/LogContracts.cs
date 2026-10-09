namespace ServerMonitor.Contracts.Logs;

public static class LogLevels
{
    public const string Trace = "Trace";
    public const string Debug = "Debug";
    public const string Information = "Information";
    public const string Warning = "Warning";
    public const string Error = "Error";
    public const string Critical = "Critical";

    public static readonly IReadOnlyList<string> All = [Trace, Debug, Information, Warning, Error, Critical];
}

public sealed record LogEntryDto(
    string Id,
    Guid ServerId,
    DateTimeOffset Time,
    string Level,
    string Source,
    string Message);

public sealed record LogSearchResultDto(IReadOnlyList<LogEntryDto> Entries, string? NextCursor);
