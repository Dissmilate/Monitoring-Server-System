namespace ServerMonitor.Application.Abstractions;

public interface IMessagePublisher
{
    Task PublishAsync(string topic, string key, byte[] value, CancellationToken ct);
}

public sealed class MessagePublishException(string message, Exception? innerException = null)
    : Exception(message, innerException);
