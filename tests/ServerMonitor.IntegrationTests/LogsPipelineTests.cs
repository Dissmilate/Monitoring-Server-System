using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Agent;
using ServerMonitor.Agent.Logs;
using ServerMonitor.Agent.Transport;
using ServerMonitor.Contracts.Auth;
using ServerMonitor.Contracts.Logs;
using ServerMonitor.IntegrationTests.Infrastructure;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.IntegrationTests;

public sealed class LogsPipelineTests(IntegrationFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow.AddSeconds(-1);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pushed_entries_are_indexed_and_searchable_newest_first()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        using var gateway = Gateway();

        var accepted = await gateway.PushLogsAsync(apiKey, Batch(
            Entry("a", Now.AddSeconds(-30), LogSeverity.Information, "service started"),
            Entry("b", Now.AddSeconds(-20), LogSeverity.Warning, "disk 91% full"),
            Entry("c", Now.AddSeconds(-10), LogSeverity.Error, "upstream timed out")), Ct);

        accepted.ShouldBe(3);
        var result = await WaitForEntriesAsync(admin, $"serverId={server.Id}", count: 3);

        result.Entries.Select(e => e.Message).ShouldBe(["upstream timed out", "disk 91% full", "service started"]);
        var newest = result.Entries[0];
        newest.Id.ShouldBe("c");
        newest.ServerId.ShouldBe(server.Id);
        newest.Level.ShouldBe(LogLevels.Error);
        newest.Source.ShouldBe("/var/log/app.log");
        newest.Time.ShouldBe(Now.AddSeconds(-10), TimeSpan.FromMilliseconds(1));
        result.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Search_filters_by_level_and_full_text()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        using var gateway = Gateway();

        await gateway.PushLogsAsync(apiKey, Batch(
            Entry("1", Now.AddSeconds(-4), LogSeverity.Information, "GET /api/orders 200"),
            Entry("2", Now.AddSeconds(-3), LogSeverity.Error, "payment provider timed out"),
            Entry("3", Now.AddSeconds(-2), LogSeverity.Warning, "slow query on orders"),
            Entry("4", Now.AddSeconds(-1), LogSeverity.Critical, "out of memory")), Ct);
        await WaitForEntriesAsync(admin, $"serverId={server.Id}", count: 4);

        (await SearchAsync(admin, $"serverId={server.Id}&levels=error,critical")).Entries.Select(e => e.Id).ShouldBe(["4", "2"]);
        (await SearchAsync(admin, $"serverId={server.Id}&q=orders")).Entries.Select(e => e.Id).ShouldBe(["3", "1"]);
        (await SearchAsync(admin, $"serverId={server.Id}&q=provider%20timed&levels=Error")).Entries.Select(e => e.Id).ShouldBe(["2"]);
    }

    [Fact]
    public async Task Pages_through_results_with_a_cursor_without_gaps_or_repeats()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        using var gateway = Gateway();

        await gateway.PushLogsAsync(apiKey, Batch(
            Entry("e1", Now.AddSeconds(-5), LogSeverity.Information, "one"),
            Entry("e2", Now.AddSeconds(-4), LogSeverity.Information, "two"),
            Entry("e3", Now.AddSeconds(-3), LogSeverity.Information, "three"),
            Entry("e4", Now.AddSeconds(-3), LogSeverity.Information, "four"),
            Entry("e5", Now.AddSeconds(-1), LogSeverity.Information, "five")), Ct);
        await WaitForEntriesAsync(admin, $"serverId={server.Id}", count: 5);

        var ids = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await SearchAsync(admin, $"serverId={server.Id}&limit=2" + (cursor is null ? string.Empty : $"&cursor={cursor}"));
            ids.AddRange(page.Entries.Select(e => e.Id));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        ids.ShouldBe(["e5", "e4", "e3", "e2", "e1"]);
        pages.ShouldBe(3);
    }

    [Fact]
    public async Task Re_sent_entries_are_stored_once()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        using var gateway = Gateway();
        var batch = Batch(Entry("x", Now.AddSeconds(-3), LogSeverity.Information, "x"), Entry("y", Now.AddSeconds(-2), LogSeverity.Information, "y"));

        await gateway.PushLogsAsync(apiKey, batch, Ct);
        await gateway.PushLogsAsync(apiKey, batch, Ct);
        await gateway.PushLogsAsync(apiKey, Batch(Entry("z", Now.AddSeconds(-1), LogSeverity.Information, "z")), Ct);

        var result = await WaitForEntriesAsync(admin, $"serverId={server.Id}", count: 3);
        result.Entries.Select(e => e.Id).ShouldBe(["z", "y", "x"]);
    }

    [Fact]
    public async Task Gateway_sanitizes_untrusted_entries()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        using var gateway = Gateway();
        var unspecified = Entry("ok", Now, LogSeverity.Unspecified, new string('m', 10_000));
        unspecified.Source = string.Empty;

        var accepted = await gateway.PushLogsAsync(apiKey, Batch(
            unspecified,
            Entry(string.Empty, Now, LogSeverity.Error, "no id"),
            Entry("future", Now.AddHours(1), LogSeverity.Error, "clock skew"),
            Entry("ancient", Now.AddDays(-30), LogSeverity.Error, "older than the retention")), Ct);

        accepted.ShouldBe(1);
        var entry = (await WaitForEntriesAsync(admin, $"serverId={server.Id}", count: 1)).Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevels.Information);
        entry.Source.ShouldBe("unknown");
        entry.Message.Length.ShouldBe(8 * 1024);
    }

    [Fact]
    public async Task Real_agent_tails_a_file_and_delivers_its_lines()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        var directory = Path.Combine(Path.GetTempPath(), $"sm-agent-it-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var logFile = Path.Combine(directory, "app.log");
        var marker = Guid.NewGuid().ToString("N");
        using var gateway = Gateway();

        try
        {
            await File.WriteAllTextAsync(logFile, $"{Now:O} INFO request {marker} served\n{Now:O} ERROR request {marker} failed\n", Ct);

            using (var queue = new SqliteLogQueue(directory, 1000, NullLogger<SqliteLogQueue>.Instance))
            {
                var source = new FileLogSource(logFile, TimeProvider.System);
                await new LogCollector([source], queue, TimeProvider.System, NullLogger<LogCollector>.Instance).PollAsync(source, Ct);
                var sender = new LogSender(new AgentSession(), queue, gateway, NullLogger<LogSender>.Instance);
                (await sender.SendNextBatchAsync(apiKey, Ct)).ShouldBe(2);
            }

            var result = await WaitForEntriesAsync(admin, $"serverId={server.Id}&q={marker}", count: 2);
            result.Entries.Select(e => e.Level).ShouldBe([LogLevels.Error, LogLevels.Information], ignoreOrder: true);
            result.Entries.ShouldAllBe(e => e.Source == logFile);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("levels=Verbose", "Logs.InvalidLevel")]
    [InlineData("cursor=garbage!", "Logs.InvalidCursor")]
    [InlineData("from=2026-10-07T12:00:00Z&to=2026-10-07T11:00:00Z", "Logs.InvalidRange")]
    public async Task Invalid_queries_are_rejected(string query, string code)
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);

        using var response = await admin.GetAsync(new Uri($"/api/logs?{query}", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe(code);
    }

    [Fact]
    public async Task Viewers_can_search_and_anonymous_users_cannot()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (email, password) = await admin.CreateUserAsync(Roles.Viewer, Ct);
        using var viewer = fixture.CreateClient();
        await viewer.LoginAsync(email, password, Ct);
        using var anonymous = fixture.CreateClient();

        (await viewer.GetAsync(new Uri("/api/logs", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await anonymous.GetAsync(new Uri("/api/logs", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private GrpcGatewayClient Gateway() => new(fixture.CreateGatewayChannel());

    private Task<LogSearchResultDto> WaitForEntriesAsync(HttpClient client, string query, int count) =>
        Eventually.GetAsync(() => SearchAsync(client, query), r => r.Entries.Count >= count, Ct);

    private async Task<LogSearchResultDto> SearchAsync(HttpClient client, string query)
    {
        var from = Uri.EscapeDataString(Now.AddDays(-1).ToString("O", CultureInfo.InvariantCulture));
        var to = Uri.EscapeDataString(Now.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture));
        return (await client.GetFromJsonAsync<LogSearchResultDto>($"/api/logs?from={from}&to={to}&{query}", Ct))!;
    }

    private static LogBatch Batch(params LogEntry[] entries)
    {
        var batch = new LogBatch { BatchId = Guid.CreateVersion7().ToString() };
        batch.Entries.Add(entries);
        return batch;
    }

    private static LogEntry Entry(string id, DateTimeOffset time, LogSeverity level, string message) => new()
    {
        Id = id,
        Time = Timestamp.FromDateTimeOffset(time),
        Level = level,
        Source = "/var/log/app.log",
        Message = message,
    };
}
