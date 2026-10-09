using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using ServerMonitor.Agent.Transport;
using ServerMonitor.Contracts.Auth;
using ServerMonitor.Contracts.Live;
using ServerMonitor.IntegrationTests.Infrastructure;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.IntegrationTests;

public sealed class DashboardTests(IntegrationFixture fixture)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_live_hub_rejects_anonymous_connections()
    {
        await using var hub = HubConnection(cookie: null);

        var error = await Should.ThrowAsync<HttpRequestException>(() => hub.StartAsync(Ct));

        error.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Watchers_get_live_samples_and_everyone_gets_status_changes()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        var samples = Channel.CreateUnbounded<LiveSampleDto>();
        var statuses = Channel.CreateUnbounded<ServerStatusChangedDto>();

        await using var hub = HubConnection(await SignInCookieAsync());
        hub.On<LiveSampleDto>(nameof(ILiveClient.MetricsReceived), s => samples.Writer.TryWrite(s));
        hub.On<ServerStatusChangedDto>(nameof(ILiveClient.ServerStatusChanged), s => statuses.Writer.TryWrite(s));
        await hub.StartAsync(Ct);
        await hub.InvokeAsync(LiveHub.WatchServer, server.Id, Ct);

        using var gateway = new GrpcGatewayClient(fixture.CreateGatewayChannel());
        var sample = await Eventually.GetAsync(
            async () =>
            {
                await gateway.PushMetricsAsync(apiKey, Batch(cpu: 37), Ct);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                wait.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    return await samples.Reader.ReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
                {
                    return null;
                }
            },
            s => s is not null,
            Ct);

        sample!.ServerId.ShouldBe(server.Id);
        sample.System.CpuPercent.ShouldBe(37);
        sample.Disks.ShouldHaveSingleItem().Mount.ShouldBe("/");

        using var call = new AgentGateway.AgentGatewayClient(fixture.CreateGatewayChannel())
            .Connect(new Metadata { { "authorization", $"ApiKey {apiKey}" } }, cancellationToken: Ct);
        await call.RequestStream.WriteAsync(new AgentMessage { Hello = new AgentHello { Host = AgentScenario.TestHost } }, Ct);

        var change = await ReadMatchingAsync(statuses.Reader, s => s.ServerId == server.Id);
        change.Status.ShouldBe("Online");
    }

    [Fact]
    public async Task Clients_that_do_not_watch_a_server_get_no_samples_for_it()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (watched, watchedKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        var (_, otherKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        var samples = Channel.CreateUnbounded<LiveSampleDto>();

        await using var hub = HubConnection(await SignInCookieAsync());
        hub.On<LiveSampleDto>(nameof(ILiveClient.MetricsReceived), s => samples.Writer.TryWrite(s));
        await hub.StartAsync(Ct);
        await hub.InvokeAsync(LiveHub.WatchServer, watched.Id, Ct);

        using var gateway = new GrpcGatewayClient(fixture.CreateGatewayChannel());
        await Eventually.GetAsync(
            async () =>
            {
                await gateway.PushMetricsAsync(otherKey, Batch(cpu: 1), Ct);
                await gateway.PushMetricsAsync(watchedKey, Batch(cpu: 2), Ct);
                await Task.Delay(500, Ct);
                return samples.Reader.Count;
            },
            count => count > 0,
            Ct);

        var received = new List<LiveSampleDto>();
        while (samples.Reader.TryRead(out var s))
        {
            received.Add(s);
        }

        received.ShouldAllBe(s => s.ServerId == watched.Id);
    }

    [Fact]
    public async Task The_overview_gets_the_newest_sample_of_each_server()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        using var gateway = new GrpcGatewayClient(fixture.CreateGatewayChannel());
        var now = DateTimeOffset.UtcNow;

        await gateway.PushMetricsAsync(apiKey, Batch(cpu: 10, time: now.AddSeconds(-20)), Ct);
        await gateway.PushMetricsAsync(apiKey, Batch(cpu: 55, time: now.AddSeconds(-10)), Ct);

        var latest = await Eventually.GetAsync(
            async () => (await admin.GetFromJsonAsync<List<ServerSummaryDto>>("/api/metrics/latest", Ct))!
                .FirstOrDefault(s => s.ServerId == server.Id && s.CpuPercent == 55),
            s => s is not null,
            Ct);

        latest!.MemoryTotalBytes.ShouldBe(8L << 30);
        latest.DiskUsedPercent.ShouldBe(25);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/servers/00000000-0000-0000-0000-000000000001")]
    [InlineData("/login")]
    public async Task Dashboard_routes_are_served_by_the_single_page_app(string path)
    {
        using var client = fixture.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("_framework/blazor.webassembly.js");
    }

    [Theory]
    [InlineData("/_framework/blazor.webassembly.js", "javascript")]
    [InlineData("/_content/MudBlazor/MudBlazor.min.css", "css")]
    public async Task The_app_bundle_is_served(string path, string contentType)
    {
        using var client = fixture.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldNotBeNull().ShouldContain(contentType);
    }

    [Fact]
    public async Task Unknown_api_paths_are_not_swallowed_by_the_dashboard()
    {
        using var client = fixture.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/does-not-exist", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private HubConnection HubConnection(string? cookie) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(fixture.Api.Server.BaseAddress, LiveHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => fixture.Api.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (cookie is not null)
                {
                    options.Headers["Cookie"] = cookie;
                }
            })
            .Build();

    private async Task<string> SignInCookieAsync()
    {
        using var client = fixture.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(IntegrationFixture.AdminEmail, IntegrationFixture.AdminPassword), Ct);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    private async Task<T> ReadMatchingAsync<T>(ChannelReader<T> reader, Func<T, bool> match)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            var item = await reader.ReadAsync(timeout.Token);
            if (match(item))
            {
                return item;
            }
        }
    }

    private static MetricBatch Batch(int cpu, DateTimeOffset? time = null) => new()
    {
        BatchId = Guid.CreateVersion7().ToString(),
        Samples =
        {
            new MetricSample
            {
                Time = Timestamp.FromDateTimeOffset(time ?? DateTimeOffset.UtcNow),
                CpuPercent = cpu,
                MemoryUsedBytes = 2L << 30,
                MemoryTotalBytes = 8L << 30,
                UptimeSeconds = 100,
                Disks = { new DiskSample { Mount = "/", UsedBytes = 25, TotalBytes = 100 } },
                Network = { new NetworkSample { Interface = "eth0", RxBytesPerSecond = 10, TxBytesPerSecond = 20 } },
            },
        },
    };
}
