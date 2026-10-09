using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Application.Agents;
using ServerMonitor.Domain.Servers;
using ServerMonitor.Gateway.Agents;
using ServerMonitor.Gateway.Auth;
using ServerMonitor.Infrastructure.Messaging;
using ServerMonitor.Protocol.Telemetry.V1;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Gateway.Services;

[Authorize(Policy = AgentAuth.Policy)]
internal sealed partial class AgentGatewayService(
    EnrollAgentHandler enrollHandler,
    IMessagePublisher publisher,
    AgentControlSession controlSession,
    TimeProvider time,
    ILogger<AgentGatewayService> logger)
    : AgentGateway.AgentGatewayBase
{
    public override Task Connect(
        IAsyncStreamReader<AgentMessage> requestStream,
        IServerStreamWriter<ServerMessage> responseStream,
        ServerCallContext context) =>
        controlSession.RunAsync(requestStream, responseStream, context.GetHttpContext().User, context.CancellationToken);

    [AllowAnonymous]
    public override async Task<EnrollResponse> Enroll(EnrollRequest request, ServerCallContext context)
    {
        var host = new AgentHost(
            request.Host?.Hostname ?? string.Empty,
            request.Host?.OsDescription ?? string.Empty,
            request.Host?.AgentVersion ?? string.Empty);

        var result = await enrollHandler.HandleAsync(
            new EnrollAgentCommand(request.EnrollmentToken, host),
            context.CancellationToken);

        if (!result.IsSuccess)
        {
            LogEnrollmentRejected(logger, context.Peer);
            throw new RpcException(new Status(StatusCode.PermissionDenied, result.Error.Message));
        }

        var enrollment = result.Value;
        LogEnrolled(logger, enrollment.ServerId, host.Hostname);

        return new EnrollResponse
        {
            ServerId = enrollment.ServerId.ToString(),
            ServerName = enrollment.ServerName,
            ApiKey = enrollment.ApiKey,
        };
    }

    public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context) =>
        Task.FromResult(new PingResponse
        {
            ServerId = context.GetHttpContext().User.GetServerId().ToString(),
            ServerTime = Timestamp.FromDateTimeOffset(time.GetUtcNow()),
        });

    public override async Task<PushAck> PushMetrics(MetricBatch request, ServerCallContext context)
    {
        if (request.Samples.Count > MetricBatchValidator.MaxSamplesPerBatch)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"A batch may contain at most {MetricBatchValidator.MaxSamplesPerBatch} samples."));
        }

        var serverId = context.GetHttpContext().User.GetServerId();
        var now = time.GetUtcNow();
        var batch = MetricBatchValidator.Sanitize(request, now);
        if (batch.Samples.Count < request.Samples.Count)
        {
            LogSamplesDropped(logger, request.Samples.Count - batch.Samples.Count, serverId);
        }

        if (batch.Samples.Count > 0)
        {
            var envelope = new MetricBatchEnvelope
            {
                ServerId = serverId.ToString(),
                ReceivedAt = Timestamp.FromDateTimeOffset(now),
                Batch = batch,
            };

            await PublishAsync(KafkaTopics.Metrics, envelope.ServerId, envelope.ToByteArray(), serverId, context.CancellationToken);
        }

        return new PushAck { Accepted = batch.Samples.Count };
    }

    public override async Task<PushAck> PushLogs(LogBatch request, ServerCallContext context)
    {
        if (request.Entries.Count > LogBatchValidator.MaxEntriesPerBatch || request.CalculateSize() > LogBatchValidator.MaxBatchBytes)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"A batch may contain at most {LogBatchValidator.MaxEntriesPerBatch} entries and {LogBatchValidator.MaxBatchBytes} bytes."));
        }

        var serverId = context.GetHttpContext().User.GetServerId();
        var now = time.GetUtcNow();
        var batch = LogBatchValidator.Sanitize(request, now);
        if (batch.Entries.Count < request.Entries.Count)
        {
            LogEntriesDropped(logger, request.Entries.Count - batch.Entries.Count, serverId);
        }

        if (batch.Entries.Count > 0)
        {
            var envelope = new LogBatchEnvelope
            {
                ServerId = serverId.ToString(),
                ReceivedAt = Timestamp.FromDateTimeOffset(now),
                Batch = batch,
            };

            await PublishAsync(KafkaTopics.Logs, envelope.ServerId, envelope.ToByteArray(), serverId, context.CancellationToken);
        }

        return new PushAck { Accepted = batch.Entries.Count };
    }

    private async Task PublishAsync(string topic, string key, byte[] payload, Guid serverId, CancellationToken ct)
    {
        try
        {
            await publisher.PublishAsync(topic, key, payload, ct);
        }
        catch (MessagePublishException ex)
        {
            LogPublishFailed(logger, ex, topic, serverId);
            throw new RpcException(new Status(StatusCode.Unavailable, "Telemetry queue is unavailable."));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent enrolled as server {ServerId} from host {Hostname}")]
    private static partial void LogEnrolled(ILogger logger, Guid serverId, string hostname);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Enrollment rejected for peer {Peer}")]
    private static partial void LogEnrollmentRejected(ILogger logger, string peer);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped {Count} invalid metric samples from server {ServerId}")]
    private static partial void LogSamplesDropped(ILogger logger, int count, Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped {Count} invalid log entries from server {ServerId}")]
    private static partial void LogEntriesDropped(ILogger logger, int count, Guid serverId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not queue {Topic} from server {ServerId}")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, string topic, Guid serverId);
}
