using System.Net.Http.Json;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Agent;
using ServerMonitor.Agent.Control;
using ServerMonitor.Agent.Credentials;
using ServerMonitor.Agent.Metrics;
using ServerMonitor.Agent.Telemetry;
using ServerMonitor.Agent.Transport;
using ServerMonitor.Contracts.Agents;
using ServerMonitor.Contracts.Servers;
using ServerMonitor.Infrastructure.Messaging;
using ServerMonitor.IntegrationTests.Infrastructure;
using ServerMonitor.Protocol.Messaging.V1;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.IntegrationTests;

public sealed class AgentControlChannelTests(IntegrationFixture fixture)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Real_agent_goes_online_receives_its_configuration_and_goes_offline_on_shutdown()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        await using var agent = await RunningAgent.StartAsync(fixture, server.Id, apiKey, Ct);

        await WaitForStatusAsync(admin, server.Id, "Online");
        await Eventually.GetAsync(() => Task.FromResult(agent.Configuration.Current), c => c.Version == 1, Ct);
        agent.Configuration.Current.HeartbeatInterval.ShouldBe(TimeSpan.FromSeconds(1));

        await agent.StopAsync();

        await WaitForStatusAsync(admin, server.Id, "Offline");
    }

    [Fact]
    public async Task A_configuration_change_reaches_a_connected_agent_and_is_acknowledged()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        await using var agent = await RunningAgent.StartAsync(fixture, server.Id, apiKey, Ct);
        await WaitForStatusAsync(admin, server.Id, "Online");

        using var update = await admin.PutAsJsonAsync(
            $"/api/servers/{server.Id}/configuration", new UpdateAgentConfigurationRequest(30), Ct);
        (await update.Content.ReadFromJsonAsync<AgentConfigurationDto>(Ct)).ShouldBe(new AgentConfigurationDto(2, 30));

        var applied = await Eventually.GetAsync(
            () => Task.FromResult(agent.Configuration.Current), c => c.Version == 2, Ct);
        applied.MetricsInterval.ShouldBe(TimeSpan.FromSeconds(30));

        var command = await WaitForCommandAsync(admin, server.Id, c => c.Type == "ApplyConfig" && c.Status == "Succeeded");
        command.ResultMessage.ShouldBe("Configuration v2 applied.");
    }

    [Fact]
    public async Task A_command_queued_while_offline_is_delivered_when_the_agent_connects()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);

        using var request = await admin.PostAsJsonAsync(
            $"/api/servers/{server.Id}/commands", new RequestAgentCommandRequest("CollectNow"), Ct);
        request.StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted);
        var queued = (await request.Content.ReadFromJsonAsync<AgentCommandDto>(Ct))!;
        queued.Status.ShouldBe("Pending");

        await using var agent = await RunningAgent.StartAsync(fixture, server.Id, apiKey, Ct);

        var done = await WaitForCommandAsync(admin, server.Id, c => c.Id == queued.Id && c.Status == "Succeeded");
        done.ResultMessage.ShouldBe("Sample collected.");
        done.DeliveredAt.ShouldNotBeNull();
        (await agent.Buffer.CountAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Connecting_updates_the_host_details_shown_in_the_dashboard()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        using var call = Client().Connect(Auth(apiKey), cancellationToken: Ct);

        var upgraded = new HostInfo { Hostname = "renamed-host", OsDescription = "Debian 13", AgentVersion = "9.9.9" };
        await call.RequestStream.WriteAsync(new AgentMessage { Hello = new AgentHello { Host = upgraded } }, Ct);

        var updated = await WaitForStatusAsync(admin, server.Id, "Online");
        updated.Hostname.ShouldBe("renamed-host");
        updated.OperatingSystem.ShouldBe("Debian 13");
        updated.AgentVersion.ShouldBe("9.9.9");
    }

    [Fact]
    public async Task A_silent_agent_is_marked_offline_and_comes_back_with_its_next_heartbeat()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        using var call = await ConnectRawAsync(apiKey);

        await WaitForStatusAsync(admin, server.Id, "Online");
        await WaitForStatusAsync(admin, server.Id, "Offline");

        await call.RequestStream.WriteAsync(new AgentMessage { Heartbeat = new Heartbeat() }, Ct);

        await WaitForStatusAsync(admin, server.Id, "Online");
    }

    [Fact]
    public async Task Revoking_the_key_ends_an_open_control_channel()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        using var call = await ConnectRawAsync(apiKey);
        (await admin.DeleteAsync(new Uri($"/api/servers/{server.Id}/agent-keys", UriKind.Relative), Ct)).EnsureSuccessStatusCode();

        await call.RequestStream.WriteAsync(new AgentMessage { Heartbeat = new Heartbeat() }, Ct);

        var error = await Should.ThrowAsync<RpcException>(() => DrainAsync(call));
        error.StatusCode.ShouldBe(StatusCode.Unauthenticated);
        await WaitForStatusAsync(admin, server.Id, "Offline");
    }

    [Fact]
    public async Task A_newer_connection_replaces_the_older_one()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        using var older = await ConnectRawAsync(apiKey);
        using var newer = await ConnectRawAsync(apiKey);

        await DrainAsync(older).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await newer.RequestStream.WriteAsync(new AgentMessage { Heartbeat = new Heartbeat() }, Ct);
        await WaitForStatusAsync(admin, server.Id, "Online");
    }

    [Fact]
    public async Task The_first_message_must_be_a_hello()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (_, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        using var call = Client().Connect(Auth(apiKey), cancellationToken: Ct);

        await call.RequestStream.WriteAsync(new AgentMessage { Heartbeat = new Heartbeat() }, Ct);

        var error = await Should.ThrowAsync<RpcException>(() => DrainAsync(call));
        error.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Status_changes_are_published_for_live_views()
    {
        using var admin = await fixture.CreateAdminClientAsync(Ct);
        var (server, apiKey) = await AgentScenario.EnrollAsync(fixture, admin, Ct);
        using var events = fixture.Gateway.Services.GetRequiredService<KafkaConsumerFactory>().CreateBroadcast("test-events");
        events.Subscribe(KafkaTopics.MonitorEvents);
        await Eventually.GetAsync(() => Task.FromResult(events.Consume(TimeSpan.FromMilliseconds(200))), _ => events.Assignment.Count > 0, Ct);

        using (await ConnectRawAsync(apiKey))
        {
            await WaitForStatusAsync(admin, server.Id, "Online");
        }

        var statuses = new List<string>();
        await Eventually.GetAsync(
            () =>
            {
                if (events.Consume(TimeSpan.FromMilliseconds(500)) is { } result
                    && result.Message.Key == server.Id.ToString())
                {
                    statuses.Add(MonitorEvent.Parser.ParseFrom(result.Message.Value).AgentStatusChanged.Status);
                }

                return Task.FromResult(statuses);
            },
            s => s.Count >= 2,
            Ct);

        statuses.ShouldBe(["Online", "Offline"]);
    }

    private AgentGateway.AgentGatewayClient Client() => new(fixture.CreateGatewayChannel());

    private static Metadata Auth(string apiKey) => new() { { "authorization", $"ApiKey {apiKey}" } };

    private async Task<AsyncDuplexStreamingCall<AgentMessage, ServerMessage>> ConnectRawAsync(string apiKey)
    {
        var call = Client().Connect(Auth(apiKey), cancellationToken: Ct);
        await call.RequestStream.WriteAsync(new AgentMessage { Hello = new AgentHello { Host = AgentScenario.TestHost } }, Ct);
        (await call.ResponseStream.MoveNext(Ct)).ShouldBeTrue();
        call.ResponseStream.Current.PayloadCase.ShouldBe(ServerMessage.PayloadOneofCase.Config);
        return call;
    }

    private async Task DrainAsync(AsyncDuplexStreamingCall<AgentMessage, ServerMessage> call)
    {
        while (await call.ResponseStream.MoveNext(Ct))
        {
        }
    }

    private Task<ServerDto> WaitForStatusAsync(HttpClient admin, Guid serverId, string status) =>
        Eventually.GetAsync(
            async () => (await admin.GetFromJsonAsync<ServerDto>($"/api/servers/{serverId}", Ct))!,
            s => s.Status == status,
            Ct,
            TimeSpan.FromSeconds(20));

    private Task<AgentCommandDto> WaitForCommandAsync(HttpClient admin, Guid serverId, Func<AgentCommandDto, bool> match) =>
        Eventually.GetAsync(
            async () => (await admin.GetFromJsonAsync<List<AgentCommandDto>>($"/api/servers/{serverId}/commands", Ct))!.FirstOrDefault(match),
            c => c is not null,
            Ct,
            TimeSpan.FromSeconds(20)).ContinueWith(t => t.Result!, TaskScheduler.Default);

    private sealed class RunningAgent : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly GrpcGatewayClient _gateway;
        private readonly MetricsSampler _sampler;
        private readonly ControlChannelService _channel;
        private bool _stopped;

        private RunningAgent(IntegrationFixture fixture, Guid serverId, string apiKey)
        {
            _directory = Path.Combine(Path.GetTempPath(), $"sm-agent-it-{Guid.NewGuid():N}");
            _gateway = new GrpcGatewayClient(fixture.CreateGatewayChannel());
            Buffer = new SqliteTelemetryBuffer(_directory, 1000, NullLogger<SqliteTelemetryBuffer>.Instance);
            Configuration = new RuntimeConfiguration(_directory, TimeSpan.FromSeconds(10), NullLogger<RuntimeConfiguration>.Instance);
            _sampler = new MetricsSampler(MetricsSources.ForCurrentPlatform(new AgentOptions(), TimeProvider.System), Buffer);

            var session = new AgentSession();
            session.Start(new AgentCredentials(serverId, apiKey));
            _channel = new ControlChannelService(
                session,
                _gateway,
                Configuration,
                new ServerMessageHandler(Configuration, _sampler),
                TimeProvider.System,
                NullLogger<ControlChannelService>.Instance);
        }

        public RuntimeConfiguration Configuration { get; }

        public SqliteTelemetryBuffer Buffer { get; }

        public static async Task<RunningAgent> StartAsync(IntegrationFixture fixture, Guid serverId, string apiKey, CancellationToken ct)
        {
            var agent = new RunningAgent(fixture, serverId, apiKey);
            await agent._channel.StartAsync(ct);
            return agent;
        }

        public async Task StopAsync()
        {
            if (!_stopped)
            {
                _stopped = true;
                await _channel.StopAsync(CancellationToken.None);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _channel.Dispose();
            _sampler.Dispose();
            Buffer.Dispose();
            _gateway.Dispose();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
