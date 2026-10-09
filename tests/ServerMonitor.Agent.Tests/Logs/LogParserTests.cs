using ServerMonitor.Agent.Logs;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Tests.Logs;

public sealed class LogParserTests
{
    [Theory]
    [InlineData("2026-10-07T12:00:00Z INFO GET /api 200", LogSeverity.Information)]
    [InlineData("2026-10-07 12:00:00,123 [WARN] disk almost full", LogSeverity.Warning)]
    [InlineData("level=error msg=\"connection refused\"", LogSeverity.Error)]
    [InlineData("Oct  7 12:00:00 host app[42]: FATAL out of memory", LogSeverity.Critical)]
    [InlineData("dbug: Microsoft.Hosting[0] DEBUG starting", LogSeverity.Debug)]
    [InlineData("INFO retrying after error", LogSeverity.Information)]
    [InlineData("plain text without a level", LogSeverity.Information)]
    [InlineData("information about the errorless run", LogSeverity.Information)]
    public void Detects_the_first_level_keyword(string line, LogSeverity expected) =>
        LogLineParser.DetectLevel(line).ShouldBe(expected);

    [Fact]
    public void Parses_an_iso_timestamp_at_the_start_of_the_line()
    {
        LogLineParser.ParseTimestamp("2026-10-07T12:00:01.250Z INFO x")
            .ShouldBe(new DateTimeOffset(2026, 10, 7, 12, 0, 1, 250, TimeSpan.Zero));
        LogLineParser.ParseTimestamp("[2026-10-07 14:00:01+02:00] x")
            .ShouldBe(new DateTimeOffset(2026, 10, 7, 12, 0, 1, TimeSpan.Zero));
        LogLineParser.ParseTimestamp("no timestamp 2026-10-07T12:00:00Z").ShouldBeNull();
    }

    [Fact]
    public void Parses_journal_entries()
    {
        const string json = """
            {"__CURSOR":"s=abc;i=1","__REALTIME_TIMESTAMP":"1791374400123456","PRIORITY":"3",
             "_SYSTEMD_UNIT":"nginx.service","MESSAGE":"upstream timed out"}
            """;

        JournalEntryParser.TryParse(json.ReplaceLineEndings(string.Empty), out var entry, out var cursor).ShouldBeTrue();

        cursor.ShouldBe("s=abc;i=1");
        entry.Time.ToDateTimeOffset().ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1791374400123).AddTicks(4560));
        entry.Level.ShouldBe(LogSeverity.Error);
        entry.Source.ShouldBe("nginx.service");
        entry.Message.ShouldBe("upstream timed out");
        entry.Id.ShouldBe(LogSources.EntryId("journald", "s=abc;i=1"));
    }

    [Fact]
    public void Journal_messages_that_are_not_utf8_text_arrive_as_byte_arrays()
    {
        const string json = """{"__CURSOR":"c","__REALTIME_TIMESTAMP":"1","SYSLOG_IDENTIFIER":"kernel","MESSAGE":[104,105]}""";

        JournalEntryParser.TryParse(json, out var entry, out _).ShouldBeTrue();

        entry.Message.ShouldBe("hi");
        entry.Source.ShouldBe("kernel");
        entry.Level.ShouldBe(LogSeverity.Information);
    }

    [Theory]
    [InlineData("""{"__REALTIME_TIMESTAMP":"1","MESSAGE":"no cursor"}""")]
    [InlineData("""{"__CURSOR":"c","MESSAGE":"no time"}""")]
    [InlineData("""{"__CURSOR":"c","__REALTIME_TIMESTAMP":"1"}""")]
    [InlineData("not json")]
    public void Incomplete_journal_entries_are_skipped(string json) =>
        JournalEntryParser.TryParse(json, out _, out _).ShouldBeFalse();
}
