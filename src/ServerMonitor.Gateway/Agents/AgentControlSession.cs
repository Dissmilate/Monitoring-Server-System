using System.Security.Claims;
using Grpc.Core;
using Microsoft.Extensions.Options;
using ServerMonitor.Application.Agents;
using ServerMonitor.Domain.Agents;
using ServerMonitor.Domain.Servers;
using ServerMonitor.Gateway.Auth;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Gateway.Agents;

internal sealed partial class AgentControlSession(
    AgentConnectionRegistry registry,
    IServiceScopeFactory scopes,
    IOptions<AgentChannelOptions> options,
    ILogger<AgentControlSession> logger)
{
    public async Task RunAsync(
        IAsyncStreamReader<AgentMessage> requests,
        IServerStreamWriter<ServerMessage> responses,
        ClaimsPrincipal agent,
        CancellationToken callCancelled)
    {
        var serverId = agent.GetServerId();
        var keyId = agent.GetKeyId();
        var hello = await ReadHelloAsync(requests, callCancelled);

        using var connection = registry.Open(serverId);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(callCancelled, connection.Closed);
        var ct = session.Token;

        try
        {
            var host = new AgentHost(
                hello.Host?.Hostname ?? string.Empty,
                hello.Host?.OsDescription ?? string.Empty,
                hello.Host?.AgentVersion ?? string.Empty);
            var start = await UseAsync(s => s.ConnectAsync(serverId, connection.Id, host, ct))
                ?? throw new RpcException(new Status(StatusCode.NotFound, "The server no longer exists."));

            LogConnected(logger, serverId, hello.Host?.AgentVersion ?? "?", hello.ConfigVersion);

            await connection.Outbound.Writer.WriteAsync(ConfigMessage(start.Config, commandId: null), ct);
            foreach (var commandId in start.PendingCommandIds)
            {
                connection.CommandSignals.Writer.TryWrite(commandId);
            }

            Task[] loops =
            [
                ReadLoopAsync(requests, connection, keyId, ct),
                WriteLoopAsync(connection, responses, ct),
                DeliveryLoopAsync(connection, ct),
                ResyncLoopAsync(connection, ct),
            ];

            await Task.WhenAny(loops);
            await session.CancelAsync();
            await Task.WhenAll(loops).ContinueWith(_ => { }, TaskScheduler.Default);

            if (callCancelled.IsCancellationRequested || connection.Closed.IsCancellationRequested)
            {
                return;
            }

            var errors = loops.Where(l => l.IsFaulted).SelectMany(l => l.Exception!.InnerExceptions).ToList();

            if (errors.OfType<RpcException>().FirstOrDefault() is { } rpcError)
            {
                throw rpcError;
            }

            if (errors.FirstOrDefault(e => e is not OperationCanceledException) is { } unexpected)
            {
                throw unexpected;
            }
        }
        finally
        {
            registry.Remove(connection);
            await UseAsync(s => s.DisconnectAsync(serverId, connection.Id, CancellationToken.None));
            LogDisconnected(logger, serverId);
        }
    }

    private async Task<AgentHello> ReadHelloAsync(IAsyncStreamReader<AgentMessage> requests, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Value.HelloTimeout);
        try
        {
            if (await requests.MoveNext(timeout.Token) && requests.Current.PayloadCase == AgentMessage.PayloadOneofCase.Hello)
            {
                return requests.Current.Hello;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "No AgentHello received in time."));
        }

        throw new RpcException(new Status(StatusCode.InvalidArgument, "The first message must be AgentHello."));
    }

    private async Task ReadLoopAsync(
        IAsyncStreamReader<AgentMessage> requests, AgentConnection connection, Guid keyId, CancellationToken ct)
    {
        while (await requests.MoveNext(ct))
        {
            var message = requests.Current;
            switch (message.PayloadCase)
            {
                case AgentMessage.PayloadOneofCase.Heartbeat:
                    var outcome = await UseAsync(s => s.HeartbeatAsync(connection.ServerId, keyId, connection.Id, ct));
                    if (outcome == HeartbeatOutcome.KeyRevoked)
                    {
                        throw new RpcException(new Status(StatusCode.Unauthenticated, "The API key was revoked."));
                    }

                    if (outcome == HeartbeatOutcome.Superseded)
                    {
                        return;
                    }

                    break;

                case AgentMessage.PayloadOneofCase.CommandResult when Guid.TryParse(message.CommandResult.CommandId, out var commandId):
                    var result = message.CommandResult;
                    await UseAsync(s => s.CompleteCommandAsync(connection.ServerId, commandId, result.Succeeded, result.Message, ct));
                    LogCommandCompleted(logger, commandId, connection.ServerId, result.Succeeded);
                    break;
            }
        }
    }

    private static async Task WriteLoopAsync(AgentConnection connection, IServerStreamWriter<ServerMessage> responses, CancellationToken ct)
    {
        await foreach (var message in connection.Outbound.Reader.ReadAllAsync(ct))
        {
            await responses.WriteAsync(message, ct);
        }
    }

    private async Task DeliveryLoopAsync(AgentConnection connection, CancellationToken ct)
    {
        // Signals can repeat (resync, re-publish): deliver once per connection.
        var delivered = new HashSet<Guid>();

        await foreach (var signal in connection.CommandSignals.Reader.ReadAllAsync(ct))
        {
            var commandIds = signal == AgentConnection.Resync
                ? await UseAsync(s => s.ListDeliverableCommandIdsAsync(connection.ServerId, ct))
                : [signal];

            foreach (var commandId in commandIds.Where(id => !delivered.Contains(id)))
            {
                var delivery = await UseAsync(s => s.PrepareDeliveryAsync(connection.ServerId, commandId, ct));
                if (delivery is null)
                {
                    continue;
                }

                delivered.Add(commandId);
                await connection.Outbound.Writer.WriteAsync(CommandMessage(delivery), ct);
                LogCommandDelivered(logger, delivery.Type, commandId, connection.ServerId);
            }
        }
    }

    private async Task ResyncLoopAsync(AgentConnection connection, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(options.Value.ResyncInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            connection.CommandSignals.Writer.TryWrite(AgentConnection.Resync);
        }
    }

    private ServerMessage CommandMessage(CommandDelivery delivery) => delivery.Type switch
    {
        AgentCommandType.ApplyConfig => ConfigMessage(delivery.Config!, delivery.CommandId),
        AgentCommandType.CollectNow => new ServerMessage { CommandId = delivery.CommandId.ToString(), CollectNow = new CollectNow() },
        _ => throw new InvalidOperationException($"Unknown command type {delivery.Type}."),
    };

    private ServerMessage ConfigMessage(AgentConfigSnapshot config, Guid? commandId) => new()
    {
        CommandId = commandId?.ToString() ?? string.Empty,
        Config = new AgentConfig
        {
            Version = config.Version,
            MetricsIntervalSeconds = config.MetricsIntervalSeconds,
            HeartbeatIntervalSeconds = (int)options.Value.HeartbeatInterval.TotalSeconds,
        },
    };

    private async Task<T> UseAsync<T>(Func<AgentConnectionService, Task<T>> action)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AgentConnectionService>());
    }

    private async Task UseAsync(Func<AgentConnectionService, Task> action)
    {
        await using var scope = scopes.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AgentConnectionService>());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent of server {ServerId} connected (agent {AgentVersion}, config v{ConfigVersion})")]
    private static partial void LogConnected(ILogger logger, Guid serverId, string agentVersion, long configVersion);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent of server {ServerId} disconnected")]
    private static partial void LogDisconnected(ILogger logger, Guid serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Delivered {Type} command {CommandId} to server {ServerId}")]
    private static partial void LogCommandDelivered(ILogger logger, AgentCommandType type, Guid commandId, Guid serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Command {CommandId} on server {ServerId} completed (success: {Succeeded})")]
    private static partial void LogCommandCompleted(ILogger logger, Guid commandId, Guid serverId, bool succeeded);
}
