using Microsoft.Extensions.Time.Testing;
using ServerMonitor.Application.Agents;
using ServerMonitor.Application.Tests.Fakes;
using ServerMonitor.Contracts.Agents;
using ServerMonitor.Domain.Agents;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Tests.Agents;

public sealed class AgentControlTests
{
    private static readonly AgentHost AgentHost = new("web-01.local", "Ubuntu 24.04", "0.4.0");

    private readonly InMemoryServerRepository _servers = new();
    private readonly InMemoryAgentCommandRepository _commands = new();
    private readonly RecordingMonitorEvents _events = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeServerPresence _presence;
    private readonly Server _server;
    private readonly AgentKey _key;

    public AgentControlTests()
    {
        _presence = new FakeServerPresence(_servers);
        _server = Server.Create("web-01", _time.GetUtcNow()).Value;
        _server.IssueEnrollmentToken("token", _time.GetUtcNow(), TimeSpan.FromHours(1));
        _key = _server.Enroll("token", new AgentHost("host", "os", "1.0"), "smk_prefix", "hash", _time.GetUtcNow()).Value;
        _servers.Add(_server);
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Changing_the_configuration_queues_exactly_one_apply_command()
    {
        var handler = new UpdateAgentConfigurationHandler(_servers, _commands, _servers, _time);

        var changed = await handler.HandleAsync(_server.Id, new UpdateAgentConfigurationRequest(30), Ct);
        var unchanged = await handler.HandleAsync(_server.Id, new UpdateAgentConfigurationRequest(30), Ct);

        changed.Value.ShouldBe(new AgentConfigurationDto(2, 30));
        unchanged.Value.ShouldBe(new AgentConfigurationDto(2, 30));
        _commands.Commands.ShouldHaveSingleItem().Type.ShouldBe(AgentCommandType.ApplyConfig);
    }

    [Fact]
    public async Task Only_collect_now_can_be_requested_and_only_for_enrolled_servers()
    {
        var handler = new RequestAgentCommandHandler(_servers, _commands, _servers, _time);
        var pending = Server.Create("not-enrolled", _time.GetUtcNow()).Value;
        _servers.Add(pending);

        (await handler.HandleAsync(_server.Id, new RequestAgentCommandRequest("ApplyConfig"), Ct))
            .Error.ShouldBe(AgentCommandErrors.UnsupportedType);
        (await handler.HandleAsync(pending.Id, new RequestAgentCommandRequest("CollectNow"), Ct))
            .Error.ShouldBe(ServerErrors.NotEnrolled);

        var queued = await handler.HandleAsync(_server.Id, new RequestAgentCommandRequest("collectnow"), Ct);

        queued.Value.Status.ShouldBe(nameof(AgentCommandStatus.Pending));
        queued.Value.ExpiresAt.ShouldBe(_time.GetUtcNow().AddMinutes(5));
    }

    [Fact]
    public async Task Connecting_reports_online_once_and_hands_over_pending_commands()
    {
        var command = AgentCommand.Create(_server.Id, AgentCommandType.CollectNow, _time.GetUtcNow());
        _commands.Add(command);
        var service = Service();

        var start = await service.ConnectAsync(_server.Id, Guid.NewGuid(), AgentHost, Ct);
        await service.ConnectAsync(_server.Id, Guid.NewGuid(), AgentHost, Ct);

        start!.Config.ShouldBe(new AgentConfigSnapshot(1, Server.DefaultMetricsIntervalSeconds));
        start.PendingCommandIds.ShouldBe([command.Id]);
        _events.StatusChanges.ShouldBe([(_server.Id, ServerStatus.Online)]);
    }

    [Fact]
    public async Task Connecting_records_the_host_the_agent_reports_after_normalizing_it()
    {
        await Service().ConnectAsync(_server.Id, Guid.NewGuid(), new AgentHost("  renamed-host ", "Debian 13", "0.5.0"), Ct);

        _presence.LastHost.ShouldBe(new AgentHost("renamed-host", "Debian 13", "0.5.0"));
    }

    [Fact]
    public async Task Heartbeats_detect_revoked_keys_and_replaced_connections()
    {
        var service = Service();
        var first = Guid.NewGuid();
        await service.ConnectAsync(_server.Id, first, AgentHost, Ct);

        (await service.HeartbeatAsync(_server.Id, _key.Id, first, Ct)).ShouldBe(HeartbeatOutcome.Alive);

        var second = Guid.NewGuid();
        await service.ConnectAsync(_server.Id, second, AgentHost, Ct);
        (await service.HeartbeatAsync(_server.Id, _key.Id, first, Ct)).ShouldBe(HeartbeatOutcome.Superseded);

        _server.RevokeAgentKeys(_time.GetUtcNow());
        (await service.HeartbeatAsync(_server.Id, _key.Id, second, Ct)).ShouldBe(HeartbeatOutcome.KeyRevoked);
    }

    [Fact]
    public async Task A_heartbeat_after_a_timeout_brings_the_server_back_online()
    {
        var service = Service();
        var connection = Guid.NewGuid();
        await service.ConnectAsync(_server.Id, connection, AgentHost, Ct);
        _presence.ExpireQuietly(_server.Id);

        await service.HeartbeatAsync(_server.Id, _key.Id, connection, Ct);

        _events.StatusChanges.Select(e => e.Status).ShouldBe([ServerStatus.Online, ServerStatus.Online]);
        _presence.StatusOf(_server.Id).ShouldBe(ServerStatus.Online);
    }

    [Fact]
    public async Task Only_the_current_connection_can_take_the_server_offline()
    {
        var service = Service();
        var old = Guid.NewGuid();
        var current = Guid.NewGuid();
        await service.ConnectAsync(_server.Id, old, AgentHost, Ct);
        await service.ConnectAsync(_server.Id, current, AgentHost, Ct);

        await service.DisconnectAsync(_server.Id, old, Ct);
        _presence.StatusOf(_server.Id).ShouldBe(ServerStatus.Online);

        await service.DisconnectAsync(_server.Id, current, Ct);
        _presence.StatusOf(_server.Id).ShouldBe(ServerStatus.Offline);
        _events.StatusChanges.Last().ShouldBe((_server.Id, ServerStatus.Offline));
    }

    [Fact]
    public async Task Apply_config_is_delivered_with_the_latest_configuration()
    {
        _server.UpdateConfiguration(20);
        var command = AgentCommand.Create(_server.Id, AgentCommandType.ApplyConfig, _time.GetUtcNow());
        _commands.Add(command);
        _server.UpdateConfiguration(40);

        var delivery = await Service().PrepareDeliveryAsync(_server.Id, command.Id, Ct);

        delivery!.Config.ShouldBe(new AgentConfigSnapshot(3, 40));
        command.Status.ShouldBe(AgentCommandStatus.Delivered);
    }

    [Fact]
    public async Task Results_complete_commands_once_and_only_for_their_own_server()
    {
        var command = AgentCommand.Create(_server.Id, AgentCommandType.CollectNow, _time.GetUtcNow());
        _commands.Add(command);
        var service = Service();

        await service.CompleteCommandAsync(Guid.NewGuid(), command.Id, succeeded: true, "spoofed", Ct);
        command.Status.ShouldBe(AgentCommandStatus.Pending);

        await service.CompleteCommandAsync(_server.Id, command.Id, succeeded: false, "disk full", Ct);
        await service.CompleteCommandAsync(_server.Id, command.Id, succeeded: true, "duplicate", Ct);

        command.Status.ShouldBe(AgentCommandStatus.Failed);
        command.ResultMessage.ShouldBe("disk full");
    }

    [Fact]
    public async Task Expired_commands_are_not_delivered()
    {
        var command = AgentCommand.Create(_server.Id, AgentCommandType.CollectNow, _time.GetUtcNow());
        _commands.Add(command);
        _time.Advance(TimeSpan.FromMinutes(5));

        (await Service().PrepareDeliveryAsync(_server.Id, command.Id, Ct)).ShouldBeNull();
    }

    private AgentConnectionService Service() => new(_servers, _commands, _presence, _events, _servers, _time);
}
