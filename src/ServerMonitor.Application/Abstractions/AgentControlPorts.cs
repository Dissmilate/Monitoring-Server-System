using ServerMonitor.Domain.Agents;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Abstractions;

public interface IAgentCommandRepository
{
    Task<AgentCommand?> GetAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<Guid>> ListDeliverableIdsAsync(Guid serverId, DateTimeOffset now, CancellationToken ct);

    Task<IReadOnlyList<AgentCommand>> ListRecentAsync(Guid serverId, int limit, CancellationToken ct);

    void Add(AgentCommand command);
}

public interface IServerPresence
{
    Task<ServerStatus?> ConnectAsync(Guid serverId, Guid connectionId, AgentHost host, DateTimeOffset now, CancellationToken ct);

    Task<ServerStatus?> HeartbeatAsync(Guid serverId, Guid connectionId, DateTimeOffset now, CancellationToken ct);

    Task<bool> DisconnectAsync(Guid serverId, Guid connectionId, CancellationToken ct);

    Task<IReadOnlyList<Guid>> ExpireAsync(DateTimeOffset cutoff, CancellationToken ct);
}

public interface ICommandOutbox
{
    Task<int> RelayPendingAsync(int batchSize, CancellationToken ct);

    Task<int> ExpireAsync(DateTimeOffset now, CancellationToken ct);
}

public interface IMonitorEvents
{
    Task AgentStatusChangedAsync(Guid serverId, ServerStatus status, DateTimeOffset at, CancellationToken ct);
}
