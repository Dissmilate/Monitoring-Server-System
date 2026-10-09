using Npgsql;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Infrastructure.Persistence;

// Each transition is a single UPDATE ... RETURNING, so connects, heartbeats and the detector cannot interleave.
internal sealed class PostgresServerPresence(NpgsqlDataSource dataSource) : IServerPresence
{
    private const string Online = nameof(ServerStatus.Online);
    private const string Offline = nameof(ServerStatus.Offline);
    private const string Pending = nameof(ServerStatus.Pending);

    public Task<ServerStatus?> ConnectAsync(
        Guid serverId, Guid connectionId, AgentHost host, DateTimeOffset now, CancellationToken ct) =>
        UpdateReturningPreviousAsync(
            $"""
            UPDATE servers AS s
            SET status = '{Online}', connection_id = @connection, last_seen_at = @now,
                hostname = @hostname, operating_system = @os, agent_version = @version
            FROM (SELECT id, status FROM servers WHERE id = @server AND status <> '{Pending}' FOR UPDATE) AS previous
            WHERE s.id = previous.id
            RETURNING previous.status
            """,
            serverId, connectionId, now, ct,
            ("hostname", host.Hostname), ("os", host.OperatingSystem), ("version", host.AgentVersion));

    public Task<ServerStatus?> HeartbeatAsync(Guid serverId, Guid connectionId, DateTimeOffset now, CancellationToken ct) =>
        UpdateReturningPreviousAsync(
            $"""
            UPDATE servers AS s
            SET status = '{Online}', last_seen_at = @now
            FROM (SELECT id, status FROM servers WHERE id = @server AND connection_id = @connection FOR UPDATE) AS previous
            WHERE s.id = previous.id
            RETURNING previous.status
            """,
            serverId, connectionId, now, ct);

    public async Task<bool> DisconnectAsync(Guid serverId, Guid connectionId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            UPDATE servers SET status = '{Offline}', connection_id = NULL
            WHERE id = @server AND connection_id = @connection AND status = '{Online}'
            """);
        command.Parameters.AddWithValue("server", serverId);
        command.Parameters.AddWithValue("connection", connectionId);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    // Keep connection_id: a stalled stream can still send a heartbeat and come back online.
    public async Task<IReadOnlyList<Guid>> ExpireAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            UPDATE servers SET status = '{Offline}'
            WHERE status = '{Online}' AND last_seen_at < @cutoff
            RETURNING id
            """);
        command.Parameters.AddWithValue("cutoff", cutoff.ToUniversalTime());

        var expired = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            expired.Add(reader.GetGuid(0));
        }

        return expired;
    }

    private async Task<ServerStatus?> UpdateReturningPreviousAsync(
        string sql, Guid serverId, Guid connectionId, DateTimeOffset now, CancellationToken ct,
        params (string Name, string Value)[] extra)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("server", serverId);
        command.Parameters.AddWithValue("connection", connectionId);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        foreach (var (name, value) in extra)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync(ct) is string previous
            ? Enum.Parse<ServerStatus>(previous)
            : null;
    }
}
