using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain.Agents;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Agents;

public sealed record AgentConfigSnapshot(long Version, int MetricsIntervalSeconds);

public sealed record AgentSessionStart(AgentConfigSnapshot Config, IReadOnlyList<Guid> PendingCommandIds);

public sealed record CommandDelivery(Guid CommandId, AgentCommandType Type, AgentConfigSnapshot? Config);

public enum HeartbeatOutcome
{
    Alive,

    Superseded,

    KeyRevoked,
}

public sealed class AgentConnectionService(
    IServerRepository servers,
    IAgentCommandRepository commands,
    IServerPresence presence,
    IMonitorEvents events,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    public async Task<AgentSessionStart?> ConnectAsync(Guid serverId, Guid connectionId, AgentHost host, CancellationToken ct)
    {
        var server = await servers.GetByIdAsync(serverId, ct);
        if (server is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var previous = await presence.ConnectAsync(serverId, connectionId, host.Normalize(), now, ct);
        if (previous is null)
        {
            return null;
        }

        if (previous != ServerStatus.Online)
        {
            await events.AgentStatusChangedAsync(serverId, ServerStatus.Online, now, ct);
        }

        var pending = await commands.ListDeliverableIdsAsync(serverId, now, ct);
        return new AgentSessionStart(Snapshot(server), pending);
    }

    public async Task<HeartbeatOutcome> HeartbeatAsync(Guid serverId, Guid keyId, Guid connectionId, CancellationToken ct)
    {
        if (!await servers.IsAgentKeyActiveAsync(keyId, ct))
        {
            return HeartbeatOutcome.KeyRevoked;
        }

        var now = time.GetUtcNow();
        var previous = await presence.HeartbeatAsync(serverId, connectionId, now, ct);
        if (previous is null)
        {
            return HeartbeatOutcome.Superseded;
        }

        if (previous != ServerStatus.Online)
        {
            await events.AgentStatusChangedAsync(serverId, ServerStatus.Online, now, ct);
        }

        return HeartbeatOutcome.Alive;
    }

    public Task<IReadOnlyList<Guid>> ListDeliverableCommandIdsAsync(Guid serverId, CancellationToken ct) =>
        commands.ListDeliverableIdsAsync(serverId, time.GetUtcNow(), ct);

    public async Task DisconnectAsync(Guid serverId, Guid connectionId, CancellationToken ct)
    {
        if (await presence.DisconnectAsync(serverId, connectionId, ct))
        {
            await events.AgentStatusChangedAsync(serverId, ServerStatus.Offline, time.GetUtcNow(), ct);
        }
    }

    public async Task<CommandDelivery?> PrepareDeliveryAsync(Guid serverId, Guid commandId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var command = await commands.GetAsync(commandId, ct);
        if (command is null || command.ServerId != serverId || !command.IsDeliverable(now))
        {
            return null;
        }

        AgentConfigSnapshot? config = null;
        if (command.Type == AgentCommandType.ApplyConfig)
        {
            var server = await servers.GetByIdAsync(serverId, ct);
            if (server is null)
            {
                return null;
            }

            config = Snapshot(server);
        }

        command.MarkDelivered(now);
        await unitOfWork.SaveChangesAsync(ct);
        return new CommandDelivery(command.Id, command.Type, config);
    }

    public async Task CompleteCommandAsync(
        Guid serverId, Guid commandId, bool succeeded, string? message, CancellationToken ct)
    {
        var command = await commands.GetAsync(commandId, ct);

        if (command is null || command.ServerId != serverId || !command.Complete(succeeded, message, time.GetUtcNow()).IsSuccess)
        {
            return;
        }

        await unitOfWork.SaveChangesAsync(ct);
    }

    private static AgentConfigSnapshot Snapshot(Server server) => new(server.ConfigVersion, server.MetricsIntervalSeconds);
}

public sealed class ExpireStalePresenceHandler(IServerPresence presence, IMonitorEvents events, TimeProvider time)
{
    public async Task<IReadOnlyList<Guid>> HandleAsync(TimeSpan offlineAfter, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var expired = await presence.ExpireAsync(now - offlineAfter, ct);
        foreach (var serverId in expired)
        {
            await events.AgentStatusChangedAsync(serverId, ServerStatus.Offline, now, ct);
        }

        return expired;
    }
}
