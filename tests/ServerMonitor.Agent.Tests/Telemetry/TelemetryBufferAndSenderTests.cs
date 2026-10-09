using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Agent.Telemetry;
using ServerMonitor.Agent.Tests.Fakes;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Tests.Telemetry;

public sealed class TelemetryBufferAndSenderTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _directory = new();
    private readonly List<SqliteTelemetryBuffer> _buffers = [];

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Samples_are_read_oldest_first_and_removed_after_delivery()
    {
        var buffer = Buffer();
        for (var i = 0; i < 3; i++)
        {
            await buffer.AppendAsync(Sample(i), Ct);
        }

        var firstTwo = await buffer.PeekAsync(2, Ct);
        firstTwo.Select(s => s.Sample.CpuPercent).ShouldBe([0, 1]);

        await buffer.RemoveThroughAsync(firstTwo[^1].Id, Ct);

        (await buffer.CountAsync(Ct)).ShouldBe(1);
        (await buffer.PeekAsync(10, Ct)).Single().Sample.CpuPercent.ShouldBe(2);
    }

    [Fact]
    public async Task A_full_buffer_drops_the_oldest_samples()
    {
        var buffer = Buffer(capacity: 5);
        for (var i = 0; i < 8; i++)
        {
            await buffer.AppendAsync(Sample(i), Ct);
        }

        (await buffer.CountAsync(Ct)).ShouldBe(5);
        (await buffer.PeekAsync(10, Ct)).Select(s => s.Sample.CpuPercent).ShouldBe([3, 4, 5, 6, 7]);
    }

    [Fact]
    public async Task Buffered_samples_survive_an_agent_restart()
    {
        await Buffer().AppendAsync(Sample(42), Ct);

        var reopened = Buffer();

        (await reopened.PeekAsync(10, Ct)).Single().Sample.CpuPercent.ShouldBe(42);
    }

    [Fact]
    public async Task Waiting_for_data_completes_when_a_sample_arrives()
    {
        var buffer = Buffer();
        var wait = buffer.WaitForDataAsync(TimeSpan.FromSeconds(30), Ct);

        await buffer.AppendAsync(Sample(1), Ct);

        (await wait.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Sender_drains_the_buffer_in_bounded_batches()
    {
        var buffer = Buffer();
        for (var i = 0; i < TelemetrySender.MaxBatchSize + 100; i++)
        {
            await buffer.AppendAsync(Sample(i % 100), Ct);
        }

        var gateway = new FakeGatewayClient();
        var sender = Sender(buffer, gateway);

        (await sender.SendNextBatchAsync("smk_key", Ct)).ShouldBe(TelemetrySender.MaxBatchSize);
        (await sender.SendNextBatchAsync("smk_key", Ct)).ShouldBe(100);
        (await sender.SendNextBatchAsync("smk_key", Ct)).ShouldBe(0);

        gateway.PushedBatches.Select(b => b.Samples.Count).ShouldBe([TelemetrySender.MaxBatchSize, 100]);
        gateway.PushedBatches.ShouldAllBe(b => Guid.Parse(b.BatchId).Version == 7);
        (await buffer.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Failed_delivery_keeps_samples_buffered()
    {
        var buffer = Buffer();
        await buffer.AppendAsync(Sample(1), Ct);
        var gateway = new FakeGatewayClient { PushFailure = StatusCode.Unavailable };

        var error = await Should.ThrowAsync<RpcException>(() => Sender(buffer, gateway).SendNextBatchAsync("smk_key", Ct));

        error.StatusCode.ShouldBe(StatusCode.Unavailable);
        (await buffer.CountAsync(Ct)).ShouldBe(1);
    }

    public void Dispose()
    {
        foreach (var buffer in _buffers)
        {
            buffer.Dispose();
        }

        _directory.Dispose();
    }

    private SqliteTelemetryBuffer Buffer(int capacity = 1000)
    {
        var buffer = new SqliteTelemetryBuffer(_directory.Path, capacity, NullLogger<SqliteTelemetryBuffer>.Instance);
        _buffers.Add(buffer);
        return buffer;
    }

    private static TelemetrySender Sender(ITelemetryBuffer buffer, FakeGatewayClient gateway) =>
        new(new AgentSession(), buffer, gateway, NullLogger<TelemetrySender>.Instance);

    private static MetricSample Sample(int marker) => new()
    {
        Time = Timestamp.FromDateTimeOffset(Start.AddSeconds(marker)),
        CpuPercent = marker,
    };
}
