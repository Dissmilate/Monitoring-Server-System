using Google.Protobuf;
using Npgsql;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain.Agents;
using ServerMonitor.Protocol.Messaging.V1;

namespace ServerMonitor.Infrastructure.Messaging;

// FOR UPDATE SKIP LOCKED: two relays never publish the same row.
internal sealed class CommandOutbox(NpgsqlDataSource dataSource, IMessagePublisher publisher) : ICommandOutbox
{
    public async Task<int> RelayPendingAsync(int batchSize, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var claimed = new List<(Guid CommandId, Guid ServerId)>();
        await using (var select = new NpgsqlCommand(
            """
            SELECT id, server_id FROM agent_commands
            WHERE published_at IS NULL
            ORDER BY created_at
            LIMIT @limit
            FOR UPDATE SKIP LOCKED
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("limit", batchSize);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                claimed.Add((reader.GetGuid(0), reader.GetGuid(1)));
            }
        }

        if (claimed.Count == 0)
        {
            return 0;
        }

        foreach (var (commandId, serverId) in claimed)
        {
            var signal = new AgentCommandSignal { CommandId = commandId.ToString(), ServerId = serverId.ToString() };
            await publisher.PublishAsync(KafkaTopics.AgentCommands, signal.ServerId, signal.ToByteArray(), ct);
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE agent_commands SET published_at = now() WHERE id = ANY(@ids)", connection, transaction))
        {
            update.Parameters.AddWithValue("ids", claimed.Select(c => c.CommandId).ToArray());
            await update.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return claimed.Count;
    }

    public async Task<int> ExpireAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            UPDATE agent_commands SET status = '{nameof(AgentCommandStatus.Expired)}', completed_at = @now
            WHERE status IN ('{nameof(AgentCommandStatus.Pending)}', '{nameof(AgentCommandStatus.Delivered)}')
              AND expires_at <= @now
            """);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        return await command.ExecuteNonQueryAsync(ct);
    }
}
