using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Agent;
using ServerMonitor.Agent.Metrics;
using ServerMonitor.Agent.Telemetry;
using ServerMonitor.Agent.Transport;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Contracts.Metrics;
using ServerMonitor.Infrastructure.Messaging;
using ServerMonitor.IntegrationTests.Infrastructure;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.IntegrationTests;

public sealed class MetricsPipelineTests(IntegrationFixture fixture)
{
    private static readonly DateTimeOffset Now = TruncateToSeconds(DateTimeOffset.UtcNow);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pushed_samples_are_stored_and_served_by_the_api()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        using var gateway = Gateway();

        var accepted = await gateway.PushMetricsAsync(
            apiKey, Batch(Sample(Now.AddSeconds(-30), 10, load: 0.5), Sample(Now.AddSeconds(-20), 20), Sample(Now.AddSeconds(-10), 30)), Ct);

        accepted.ShouldBe(3);
        var metrics = await WaitForSystemPointsAsync(admin, server.Id, count: 3);

        metrics.Resolution.ShouldBe("raw");
        metrics.System.Select(p => p.Time).ShouldBe([Now.AddSeconds(-30), Now.AddSeconds(-20), Now.AddSeconds(-10)]);
        metrics.System.Select(p => p.CpuPercent).ShouldBe([10, 20, 30]);
        metrics.System[0].LoadAverage1m.ShouldBe(0.5);
        metrics.System[1].LoadAverage1m.ShouldBeNull();
        metrics.System[2].MemoryUsedBytes.ShouldBe(30 * Gib);
        metrics.System[2].MemoryTotalBytes.ShouldBe(64 * Gib);

        var disk = metrics.Disks.ShouldHaveSingleItem();
        disk.Mount.ShouldBe("/");
        disk.Points.Select(p => p.UsedBytes).ShouldBe([10 * Gib, 20 * Gib, 30 * Gib]);

        var nic = metrics.Network.ShouldHaveSingleItem();
        nic.Interface.ShouldBe("eth0");
        nic.Points[^1].RxBytesPerSecond.ShouldBe(300);
        nic.Points[^1].TxBytesPerSecond.ShouldBe(600);
    }

    [Fact]
    public async Task Re_sent_samples_are_not_duplicated()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        using var gateway = Gateway();
        var batch = Batch(Sample(Now.AddSeconds(-40), 10), Sample(Now.AddSeconds(-30), 20));

        await gateway.PushMetricsAsync(apiKey, batch, Ct);
        await gateway.PushMetricsAsync(apiKey, batch, Ct);
        await gateway.PushMetricsAsync(apiKey, Batch(Sample(Now.AddSeconds(-20), 30)), Ct);

        var metrics = await WaitForSystemPointsAsync(admin, server.Id, count: 3);
        metrics.System.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Gateway_drops_samples_with_implausible_timestamps()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (_, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        using var gateway = Gateway();

        var accepted = await gateway.PushMetricsAsync(
            apiKey, Batch(Sample(Now.AddHours(1), 10), Sample(Now.AddDays(-30), 20), Sample(Now, 30)), Ct);

        accepted.ShouldBe(1);
    }

    [Fact]
    public async Task Minute_resolution_is_served_from_continuous_aggregates()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        using var gateway = Gateway();
        var minute = new DateTimeOffset(Now.Year, Now.Month, Now.Day, Now.Hour, Now.Minute, 0, TimeSpan.Zero).AddMinutes(-2);

        await gateway.PushMetricsAsync(
            apiKey, Batch(Sample(minute.AddSeconds(10), 10), Sample(minute.AddSeconds(20), 20), Sample(minute.AddSeconds(30), 60)), Ct);
        await WaitForSystemPointsAsync(admin, server.Id, count: 3);

        var aggregated = await GetMetricsAsync(admin, server.Id, "1m");

        aggregated.Resolution.ShouldBe("1m");
        var bucket = aggregated.System.ShouldHaveSingleItem();
        bucket.Time.ShouldBe(minute);
        bucket.CpuPercent.ShouldBe(30);
        bucket.CpuPercentMax.ShouldBe(60);
        aggregated.Disks.Single().Points.ShouldHaveSingleItem().UsedBytes.ShouldBe(30 * Gib);
    }

    [Fact]
    public async Task A_poison_message_goes_to_the_dead_letter_topic_without_blocking_the_partition()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        var publisher = fixture.Gateway.Services.GetRequiredService<IMessagePublisher>();

        await publisher.PublishAsync(KafkaTopics.Metrics, server.Id.ToString(), [0xFF, 0x00, 0x13, 0x37], Ct);
        using var gateway = Gateway();
        await gateway.PushMetricsAsync(apiKey, Batch(Sample(Now.AddSeconds(-5), 42)), Ct);

        (await WaitForSystemPointsAsync(admin, server.Id, count: 1)).System.Single().CpuPercent.ShouldBe(42);

        using var deadLetters = fixture.Gateway.Services.GetRequiredService<KafkaConsumerFactory>()
            .Create($"test-dlq-{Guid.NewGuid():N}");
        deadLetters.Subscribe(KafkaTopics.MetricsDeadLetter);
        var parked = await Eventually.GetAsync(
            () => Task.FromResult(deadLetters.Consume(TimeSpan.FromSeconds(1))),
            result => result?.Message.Key == server.Id.ToString(),
            Ct);
        parked!.Message.Value.ShouldBe(new byte[] { 0xFF, 0x00, 0x13, 0x37 });
    }

    [Fact]
    public async Task Real_agent_collects_buffers_and_delivers_samples_from_this_machine()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"sm-agent-it-{Guid.NewGuid():N}");
        using var gateway = Gateway();

        try
        {
            using (var buffer = new SqliteTelemetryBuffer(dataDirectory, 100, NullLogger<SqliteTelemetryBuffer>.Instance))
            {
                var source = MetricsSources.ForCurrentPlatform(new AgentOptions(), TimeProvider.System);
                source.Collect();
                await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
                await buffer.AppendAsync(source.Collect(), Ct);

                var sender = new TelemetrySender(new AgentSession(), buffer, gateway, NullLogger<TelemetrySender>.Instance);
                (await sender.SendNextBatchAsync(apiKey, Ct)).ShouldBe(1);
                (await buffer.CountAsync(Ct)).ShouldBe(0);
            }

            var sample = (await WaitForSystemPointsAsync(admin, server.Id, count: 1)).System.Single();
            sample.CpuPercent.ShouldBeInRange(0, 100);
            sample.MemoryTotalBytes.ShouldBeGreaterThan(0);
            sample.MemoryUsedBytes.ShouldBeInRange(1, sample.MemoryTotalBytes);
            sample.UptimeSeconds.ShouldBeGreaterThan(0);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("from=2026-10-07T12:00:00Z&to=2026-10-07T11:00:00Z", HttpStatusCode.BadRequest, "Metrics.InvalidRange")]
    [InlineData("resolution=5m", HttpStatusCode.BadRequest, "Metrics.InvalidResolution")]
    public async Task Invalid_queries_are_rejected(string query, HttpStatusCode status, string code)
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, _) = await AgentScenario.CreateServerWithTokenAsync(admin, Ct);

        using var response = await admin.GetAsync(new Uri($"/api/servers/{server.Id}/metrics?{query}", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(status);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe(code);
    }

    [Fact]
    public async Task Metrics_of_an_unknown_server_are_not_found()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);

        using var response = await admin.GetAsync(new Uri($"/api/servers/{Guid.NewGuid()}/metrics", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private const long Gib = 1024L * 1024 * 1024;

    private GrpcGatewayClient Gateway() => new(fixture.CreateGatewayChannel());

    private Task<MetricsDto> WaitForSystemPointsAsync(HttpClient admin, Guid serverId, int count) =>
        Eventually.GetAsync(() => GetMetricsAsync(admin, serverId, "raw"), m => m.System.Count >= count, Ct);

    private async Task<MetricsDto> GetMetricsAsync(HttpClient admin, Guid serverId, string resolution)
    {
        var from = Uri.EscapeDataString(Now.AddMinutes(-10).ToString("O", CultureInfo.InvariantCulture));
        var to = Uri.EscapeDataString(Now.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture));
        return (await admin.GetFromJsonAsync<MetricsDto>(
            $"/api/servers/{serverId}/metrics?from={from}&to={to}&resolution={resolution}", Ct))!;
    }

    private static MetricBatch Batch(params MetricSample[] samples)
    {
        var batch = new MetricBatch { BatchId = Guid.CreateVersion7().ToString() };
        batch.Samples.Add(samples);
        return batch;
    }

    private static MetricSample Sample(DateTimeOffset time, int cpu, double? load = null)
    {
        var sample = new MetricSample
        {
            Time = Timestamp.FromDateTimeOffset(time),
            CpuPercent = cpu,
            MemoryUsedBytes = cpu * Gib,
            MemoryTotalBytes = 64 * Gib,
            SwapUsedBytes = 0,
            UptimeSeconds = 3600,
            Disks = { new DiskSample { Mount = "/", UsedBytes = cpu * Gib, TotalBytes = 100 * Gib } },
            Network = { new NetworkSample { Interface = "eth0", RxBytesPerSecond = cpu * 10, TxBytesPerSecond = cpu * 20 } },
        };

        if (load is { } value)
        {
            sample.LoadAverage1M = value;
        }

        return sample;
    }

    private static DateTimeOffset TruncateToSeconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
}
