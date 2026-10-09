using Microsoft.Extensions.Time.Testing;
using ServerMonitor.Application.Logs;
using ServerMonitor.Contracts.Logs;

namespace ServerMonitor.Application.Tests.Logs;

public sealed class SearchLogsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly RecordingLogReader _reader = new();
    private readonly FakeTimeProvider _time = new(Now);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Defaults_to_the_last_day_and_fifty_entries()
    {
        var result = await Handler().HandleAsync(new SearchLogsRequest(null, null, "  ", null, null, null, null), Ct);

        result.IsSuccess.ShouldBeTrue();
        var query = _reader.Query.ShouldNotBeNull();
        (query.From, query.To, query.Limit).ShouldBe((Now.AddDays(-1), Now, 50));
        query.ServerId.ShouldBeNull();
        query.Levels.ShouldBeEmpty();
        query.Text.ShouldBeNull();
        query.After.ShouldBeNull();
    }

    [Fact]
    public async Task Normalizes_levels_text_and_limit()
    {
        var serverId = Guid.NewGuid();

        await Handler().HandleAsync(new SearchLogsRequest(serverId, "error, WARNING", " timeout ", null, null, 1000, null), Ct);

        var query = _reader.Query.ShouldNotBeNull();
        query.ServerId.ShouldBe(serverId);
        query.Levels.ShouldBe([LogLevels.Error, LogLevels.Warning]);
        query.Text.ShouldBe("timeout");
        query.Limit.ShouldBe(SearchLogsHandler.MaxLimit);
    }

    [Fact]
    public async Task A_cursor_round_trips()
    {
        var cursor = new LogCursor(1_791_374_400_123, "abc:def").Encode();

        await Handler().HandleAsync(new SearchLogsRequest(null, null, null, null, null, null, cursor), Ct);

        _reader.Query!.After.ShouldBe(new LogCursor(1_791_374_400_123, "abc:def"));
    }

    [Theory]
    [InlineData("Verbose", null, null, "Logs.InvalidLevel")]
    [InlineData(null, "not-a-cursor!", null, "Logs.InvalidCursor")]
    [InlineData(null, null, 201, "Logs.TextTooLong")]
    public async Task Invalid_requests_are_rejected(string? levels, string? cursor, int? textLength, string code)
    {
        var text = textLength is { } length ? new string('a', length) : null;

        var result = await Handler().HandleAsync(new SearchLogsRequest(null, levels, text, null, null, null, cursor), Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldNotBeNull().Code.ShouldBe(code);
        _reader.Query.ShouldBeNull();
    }

    [Fact]
    public async Task An_inverted_range_is_rejected()
    {
        var result = await Handler().HandleAsync(new SearchLogsRequest(null, null, null, Now, Now.AddHours(-1), null, null), Ct);

        result.Error.ShouldBe(LogErrors.InvalidRange);
    }

    private SearchLogsHandler Handler() => new(_reader, _time);

    private sealed class RecordingLogReader : ILogReader
    {
        public LogQuery? Query { get; private set; }

        public Task<LogSearchResultDto> SearchAsync(LogQuery query, CancellationToken ct)
        {
            Query = query;
            return Task.FromResult(new LogSearchResultDto([], null));
        }
    }
}
