using Grpc.Core;
using ServerMonitor.Agent.Enrollment;
using ServerMonitor.Agent.Transport;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Control;

internal sealed partial class ControlChannelService(
    AgentSession session,
    IGatewayClient gateway,
    RuntimeConfiguration configuration,
    ServerMessageHandler handler,
    TimeProvider time,
    ILogger<ControlChannelService> logger)
    : BackgroundService
{
    private readonly ExponentialBackoff _backoff = new(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var credentials = await session.WaitForCredentialsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(credentials.ApiKey, stoppingToken);
                LogStreamEnded(logger);
            }
            catch (RpcException ex) when (!stoppingToken.IsCancellationRequested)
            {
                if (ex.StatusCode == StatusCode.Unauthenticated)
                {
                    LogKeyRejected(logger);
                }
                else
                {
                    LogStreamFailed(logger, ex.StatusCode, ex.Status.Detail);
                }
            }

            await Task.Delay(_backoff.Next(), time, stoppingToken);
        }
    }

    internal async Task RunSessionAsync(string apiKey, CancellationToken ct)
    {
        using var call = gateway.Connect(apiKey, ct);
        using var writeGate = new SemaphoreSlim(1, 1);

        var hello = new AgentHello { Host = EnrollmentService.DescribeHost(), ConfigVersion = configuration.Current.Version };
        await WriteAsync(call, new AgentMessage { Hello = hello }, writeGate, ct);

        using var heartbeatsCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeats = HeartbeatLoopAsync(call, writeGate, heartbeatsCancellation.Token);
        var connected = false;

        try
        {
            await foreach (var message in call.ResponseStream.ReadAllAsync(ct))
            {
                if (!connected)
                {
                    connected = true;
                    _backoff.Reset();
                    LogConnected(logger);
                }

                var result = await handler.HandleAsync(message, ct);
                if (result is not null)
                {
                    await WriteAsync(call, new AgentMessage { CommandResult = result }, writeGate, ct);
                    LogCommandHandled(logger, message.PayloadCase.ToString(), result.CommandId, result.Succeeded);
                }
            }
        }
        finally
        {
            await heartbeatsCancellation.CancelAsync();
            try
            {
                await heartbeats;
            }
            catch (Exception ex) when (ex is OperationCanceledException or RpcException)
            {
            }
        }
    }

    private async Task HeartbeatLoopAsync(
        AsyncDuplexStreamingCall<AgentMessage, ServerMessage> call, SemaphoreSlim writeGate, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(configuration.Current.HeartbeatInterval, time);
        void OnChanged(RuntimeConfig config) => timer.Period = config.HeartbeatInterval;
        configuration.Changed += OnChanged;

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await WriteAsync(call, new AgentMessage { Heartbeat = new Heartbeat() }, writeGate, ct);
            }
        }
        finally
        {
            configuration.Changed -= OnChanged;
        }
    }

    // gRPC allows only one pending write on a request stream.
    private static async Task WriteAsync(
        AsyncDuplexStreamingCall<AgentMessage, ServerMessage> call, AgentMessage message, SemaphoreSlim writeGate, CancellationToken ct)
    {
        await writeGate.WaitAsync(ct);
        try
        {
            await call.RequestStream.WriteAsync(message, ct);
        }
        finally
        {
            writeGate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Control channel connected")]
    private static partial void LogConnected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Control channel closed by the server; reconnecting")]
    private static partial void LogStreamEnded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Control channel failed with {StatusCode} ({Detail}); reconnecting")]
    private static partial void LogStreamFailed(ILogger logger, StatusCode statusCode, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Control channel rejected the API key (revoked?); retrying later")]
    private static partial void LogKeyRejected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Handled {Kind} command {CommandId} (success: {Succeeded})")]
    private static partial void LogCommandHandled(ILogger logger, string kind, string commandId, bool succeeded);
}
