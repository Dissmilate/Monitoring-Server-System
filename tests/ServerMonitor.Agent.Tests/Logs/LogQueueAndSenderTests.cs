using System.Runtime.Versioning;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Agent.Logs;
using ServerMonitor.Agent.Tests.Fakes;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Tests.Logs;

public sealed class LogQueueAndSenderTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly List<SqliteLogQueue> _queues = [];

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Entries_and_the_source_position_are_stored_together_and_survive_a_restart()
    {
        await Queue().AppendAsync("file:/a.log", [Entry("1"), Entry("2")], "42:abc", Ct);

        var reopened = Queue();

        (await reopened.GetPositionAsync("file:/a.log", Ct)).ShouldBe("42:abc");
        (await reopened.GetPositionAsync("file:/b.log", Ct)).ShouldBeNull();
        (await reopened.PeekAsync(10, int.MaxValue, Ct)).Select(e => e.Entry.Id).ShouldBe(["1", "2"]);
    }

    [Fact]
    public async Task Peek_respects_the_byte_budget_but_always_returns_one_entry()
    {
        var queue = Queue();
        await queue.AppendAsync("s", [Entry("1", new string('a', 1000)), Entry("2", new string('b', 1000))], "p", Ct);

        (await queue.PeekAsync(10, 1500, Ct)).Select(e => e.Entry.Id).ShouldBe(["1"]);
        (await queue.PeekAsync(10, 10, Ct)).Select(e => e.Entry.Id).ShouldBe(["1"]);
        (await queue.PeekAsync(10, 5000, Ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_full_queue_drops_the_oldest_entries()
    {
        var queue = Queue(capacity: 3);
        await queue.AppendAsync("s", [.. Enumerable.Range(1, 5).Select(i => Entry(i.ToString()))], "p", Ct);

        (await queue.PeekAsync(10, int.MaxValue, Ct)).Select(e => e.Entry.Id).ShouldBe(["3", "4", "5"]);
    }

    [Fact]
    public async Task Sender_delivers_a_batch_and_removes_it()
    {
        var queue = Queue();
        var gateway = new FakeGatewayClient();
        await queue.AppendAsync("s", [Entry("1"), Entry("2")], "p", Ct);

        (await Sender(queue, gateway).SendNextBatchAsync("key", Ct)).ShouldBe(2);

        gateway.PushedLogBatches.ShouldHaveSingleItem().Entries.Select(e => e.Id).ShouldBe(["1", "2"]);
        (await queue.PeekAsync(10, int.MaxValue, Ct)).ShouldBeEmpty();
        (await Sender(queue, gateway).SendNextBatchAsync("key", Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Entries_stay_queued_while_the_gateway_is_unavailable()
    {
        var queue = Queue();
        var gateway = new FakeGatewayClient { PushLogsFailure = StatusCode.Unavailable };
        await queue.AppendAsync("s", [Entry("1")], "p", Ct);

        await Should.ThrowAsync<RpcException>(() => Sender(queue, gateway).SendNextBatchAsync("key", Ct));

        (await queue.PeekAsync(10, int.MaxValue, Ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_batch_the_gateway_can_never_accept_is_dropped_instead_of_blocking_the_queue()
    {
        var queue = Queue();
        var gateway = new FakeGatewayClient { PushLogsFailure = StatusCode.InvalidArgument };
        await queue.AppendAsync("s", [Entry("1")], "p", Ct);

        (await Sender(queue, gateway).SendNextBatchAsync("key", Ct)).ShouldBe(1);

        (await queue.PeekAsync(10, int.MaxValue, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Collector_drains_a_source_in_bounded_reads_and_stores_its_position()
    {
        var queue = Queue();
        var source = new ScriptedSource(LogCollector.MaxEntriesPerRead + 10);

        await Collector(queue, source).PollAsync(source, Ct);

        source.Reads.ShouldBe(2);
        (await queue.PeekAsync(10_000, int.MaxValue, Ct)).Count.ShouldBe(LogCollector.MaxEntriesPerRead + 10);
        (await queue.GetPositionAsync(source.Key, Ct)).ShouldBe((LogCollector.MaxEntriesPerRead + 10).ToString());
    }

    [Fact]
    public async Task Collector_survives_a_failing_source()
    {
        var queue = Queue();
        var source = new ScriptedSource(0) { Failure = new IOException("permission denied") };

        await Collector(queue, source).PollAsync(source, Ct);

        (await queue.GetPositionAsync(source.Key, Ct)).ShouldBeNull();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Windows_event_log_is_readable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Event Log.");

        var result = await new WindowsEventLogSource("Application").ReadAsync(null, 10, Ct);

        result.ShouldNotBeNull().Entries.Count.ShouldBeLessThanOrEqualTo(10);
        result.Entries.ShouldAllBe(e => e.Source.StartsWith("Application/", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        foreach (var queue in _queues)
        {
            queue.Dispose();
        }

        _directory.Dispose();
    }

    private SqliteLogQueue Queue(int capacity = 1000)
    {
        var queue = new SqliteLogQueue(_directory.Path, capacity, NullLogger<SqliteLogQueue>.Instance);
        _queues.Add(queue);
        return queue;
    }

    private static LogSender Sender(ILogQueue queue, FakeGatewayClient gateway) =>
        new(new AgentSession(), queue, gateway, NullLogger<LogSender>.Instance);

    private static LogCollector Collector(ILogQueue queue, ILogSource source) =>
        new([source], queue, TimeProvider.System, NullLogger<LogCollector>.Instance);

    private static LogEntry Entry(string id, string message = "message") => new()
    {
        Id = id,
        Time = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        Level = LogSeverity.Information,
        Source = "test",
        Message = message,
    };

    private sealed class ScriptedSource(int total) : ILogSource
    {
        public int Reads { get; private set; }

        public Exception? Failure { get; init; }

        public string Key => "scripted";

        public Task<LogReadResult?> ReadAsync(string? position, int maxEntries, CancellationToken ct)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Reads++;
            var start = position is null ? 0 : int.Parse(position, System.Globalization.CultureInfo.InvariantCulture);
            var count = Math.Min(maxEntries, total - start);
            if (count <= 0)
            {
                return Task.FromResult<LogReadResult?>(null);
            }

            var entries = Enumerable.Range(start, count).Select(i => Entry(i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
            return Task.FromResult<LogReadResult?>(new LogReadResult(entries, (start + count).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }
}
