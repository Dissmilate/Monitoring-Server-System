using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain.Servers;
using ServerMonitor.Protocol.Messaging.V1;

namespace ServerMonitor.Infrastructure.Messaging;

internal sealed partial class KafkaMonitorEvents(IMessagePublisher publisher, ILogger<KafkaMonitorEvents> logger) : IMonitorEvents
{
    public async Task AgentStatusChangedAsync(Guid serverId, ServerStatus status, DateTimeOffset at, CancellationToken ct)
    {
        var message = new MonitorEvent
        {
            ServerId = serverId.ToString(),
            Time = Timestamp.FromDateTimeOffset(at),
            AgentStatusChanged = new AgentStatusChanged { Status = status.ToString() },
        };

        try
        {
            await publisher.PublishAsync(KafkaTopics.MonitorEvents, message.ServerId, message.ToByteArray(), ct);
        }
        catch (MessagePublishException ex)
        {
            LogPublishFailed(logger, ex, serverId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not publish a monitor event for server {ServerId}")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, Guid serverId);
}
