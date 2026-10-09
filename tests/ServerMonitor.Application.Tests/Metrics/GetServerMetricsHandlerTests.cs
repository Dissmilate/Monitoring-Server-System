using Microsoft.Extensions.Time.Testing;
using ServerMonitor.Application.Metrics;
using ServerMonitor.Application.Tests.Fakes;
using ServerMonitor.Contracts.Live;
using ServerMonitor.Contracts.Metrics;
using ServerMonitor.Domain.Common;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Tests.Metrics;

public sealed class GetServerMetricsHandlerTests
{
    private readonly InMemoryServerRepository _servers = new();
    private readonly RecordingReader _reader = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _serverId;

    public GetServerMetricsHandlerTests()
    {
        var server = Server.Create("web-01", _time.GetUtcNow()).Value;
        _servers.Add(server);
        _serverId = server.Id;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(60, MetricResolution.Raw)]
    [InlineData(180, MetricResolution.Raw)]
    [InlineData(181, MetricResolution.Minute)]
    [InlineData(3 * 24 * 60, MetricResolution.Minute)]
    [InlineData((3 * 24 * 60) + 1, MetricResolution.Hour)]
    public void Auto_resolution_keeps_charts_small(int minutes, MetricResolution expected) =>
        GetServerMetricsHandler.ChooseResolution(TimeSpan.FromMinutes(minutes)).ShouldBe(expected);

    [Fact]
    public async Task Defaults_to_the_last_hour_of_raw_samples()
    {
        var result = await Handler().HandleAsync(new GetServerMetricsQuery(_serverId, null, null, null), Ct);

        result.IsSuccess.ShouldBeTrue();
        _reader.Calls.ShouldHaveSingleItem().ShouldBe((_serverId, _time.GetUtcNow().AddHours(-1), _time.GetUtcNow(), MetricResolution.Raw));
    }

    [Theory]
    [InlineData("1h", MetricResolution.Hour)]
    [InlineData("1M", MetricResolution.Minute)]
    [InlineData(" raw ", MetricResolution.Raw)]
    public async Task Explicit_resolution_overrides_auto(string resolution, MetricResolution expected)
    {
        await Handler().HandleAsync(new GetServerMetricsQuery(_serverId, null, null, resolution), Ct);

        _reader.Calls.Single().Resolution.ShouldBe(expected);
    }

    [Fact]
    public async Task Inverted_or_oversized_ranges_are_rejected()
    {
        var now = _time.GetUtcNow();

        (await Handler().HandleAsync(new GetServerMetricsQuery(_serverId, now, now.AddHours(-1), null), Ct))
            .Error.ShouldBe(MetricErrors.InvalidRange);
        (await Handler().HandleAsync(new GetServerMetricsQuery(_serverId, now.AddDays(-400), now, null), Ct))
            .Error.ShouldBe(MetricErrors.InvalidRange);
        _reader.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_resolution_and_unknown_server_are_reported()
    {
        (await Handler().HandleAsync(new GetServerMetricsQuery(_serverId, null, null, "5m"), Ct))
            .Error.ShouldBe(MetricErrors.InvalidResolution);
        (await Handler().HandleAsync(new GetServerMetricsQuery(Guid.NewGuid(), null, null, null), Ct))
            .Error!.Type.ShouldBe(ErrorType.NotFound);
    }

    private GetServerMetricsHandler Handler() => new(_servers, _reader, _time);

    private sealed class RecordingReader : IMetricReader
    {
        public List<(Guid ServerId, DateTimeOffset From, DateTimeOffset To, MetricResolution Resolution)> Calls { get; } = [];

        public Task<MetricsDto> ReadAsync(
            Guid serverId, DateTimeOffset from, DateTimeOffset to, MetricResolution resolution, CancellationToken ct)
        {
            Calls.Add((serverId, from, to, resolution));
            return Task.FromResult(new MetricsDto(resolution.ToString(), from, to, [], [], []));
        }

        public Task<IReadOnlyList<ServerSummaryDto>> ReadLatestAsync(DateTimeOffset since, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ServerSummaryDto>>([]);
    }
}
