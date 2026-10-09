using System.Collections.Concurrent;
using System.Threading.Channels;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Gateway.Agents;

internal sealed class AgentConnection(Guid serverId) : IDisposable
{
    public static readonly Guid Resync = Guid.Empty;

    private readonly CancellationTokenSource _closed = new();

    public Guid ServerId { get; } = serverId;

    public Guid Id { get; } = Guid.CreateVersion7();

    public CancellationToken Closed => _closed.Token;

    public Channel<Guid> CommandSignals { get; } = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public Channel<ServerMessage> Outbound { get; } =
        Channel.CreateBounded<ServerMessage>(new BoundedChannelOptions(64) { SingleReader = true });

    public void Close()
    {
        try
        {
            _closed.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose() => _closed.Dispose();
}

internal sealed class AgentConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, AgentConnection> _connections = new();

    public int Count => _connections.Count;

    public AgentConnection Open(Guid serverId)
    {
        var connection = new AgentConnection(serverId);
        _connections.AddOrUpdate(serverId, connection, (_, previous) =>
        {
            previous.Close();
            return connection;
        });
        return connection;
    }

    public void Remove(AgentConnection connection) =>
        _connections.TryRemove(new KeyValuePair<Guid, AgentConnection>(connection.ServerId, connection));

    public bool TrySignal(Guid serverId, Guid commandId) =>
        _connections.TryGetValue(serverId, out var connection) && connection.CommandSignals.Writer.TryWrite(commandId);

    public void ResyncAll()
    {
        foreach (var connection in _connections.Values)
        {
            connection.CommandSignals.Writer.TryWrite(AgentConnection.Resync);
        }
    }
}
