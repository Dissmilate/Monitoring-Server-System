using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain.Agents;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Tests.Fakes;

internal sealed class InMemoryAgentCommandRepository : IAgentCommandRepository
{
    public List<AgentCommand> Commands { get; } = [];

    public Task<AgentCommand?> GetAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Commands.FirstOrDefault(c => c.Id == id));

    public Task<IReadOnlyList<Guid>> ListDeliverableIdsAsync(Guid serverId, DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. Commands.Where(c => c.ServerId == serverId && c.IsDeliverable(now)).Select(c => c.Id)]);

    public Task<IReadOnlyList<AgentCommand>> ListRecentAsync(Guid serverId, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AgentCommand>>([.. Commands.Where(c => c.ServerId == serverId).Reverse().Take(limit)]);

    public void Add(AgentCommand command) => Commands.Add(command);
}

internal sealed class FakeServerPresence(InMemoryServerRepository servers) : IServerPresence
{
    private readonly Dictionary<Guid, (ServerStatus Status, Guid? Connection)> _state = [];

    public ServerStatus StatusOf(Guid serverId) => _state.TryGetValue(serverId, out var s) ? s.Status : ServerStatus.Offline;

    public AgentHost? LastHost { get; private set; }

    public Task<ServerStatus?> ConnectAsync(Guid serverId, Guid connectionId, AgentHost host, DateTimeOffset now, CancellationToken ct)
    {
        LastHost = host;
        if (servers.Servers.All(s => s.Id != serverId))
        {
            return Task.FromResult<ServerStatus?>(null);
        }

        var previous = StatusOf(serverId);
        _state[serverId] = (ServerStatus.Online, connectionId);
        return Task.FromResult<ServerStatus?>(previous);
    }

    public Task<ServerStatus?> HeartbeatAsync(Guid serverId, Guid connectionId, DateTimeOffset now, CancellationToken ct)
    {
        if (!_state.TryGetValue(serverId, out var state) || state.Connection != connectionId)
        {
            return Task.FromResult<ServerStatus?>(null);
        }

        _state[serverId] = (ServerStatus.Online, connectionId);
        return Task.FromResult<ServerStatus?>(state.Status);
    }

    public Task<bool> DisconnectAsync(Guid serverId, Guid connectionId, CancellationToken ct)
    {
        if (!_state.TryGetValue(serverId, out var state) || state.Connection != connectionId || state.Status != ServerStatus.Online)
        {
            return Task.FromResult(false);
        }

        _state[serverId] = (ServerStatus.Offline, null);
        return Task.FromResult(true);
    }

    public void ExpireQuietly(Guid serverId) => _state[serverId] = (ServerStatus.Offline, _state[serverId].Connection);

    public Task<IReadOnlyList<Guid>> ExpireAsync(DateTimeOffset cutoff, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);
}

internal sealed class RecordingMonitorEvents : IMonitorEvents
{
    public List<(Guid ServerId, ServerStatus Status)> StatusChanges { get; } = [];

    public Task AgentStatusChangedAsync(Guid serverId, ServerStatus status, DateTimeOffset at, CancellationToken ct)
    {
        StatusChanges.Add((serverId, status));
        return Task.CompletedTask;
    }
}
