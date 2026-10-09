using Confluent.Kafka;
using Microsoft.Extensions.Options;
using ServerMonitor.Application.Abstractions;

namespace ServerMonitor.Infrastructure.Messaging;

internal sealed class KafkaMessagePublisher : IMessagePublisher, IDisposable
{
    private readonly IProducer<string, byte[]> _producer;

    public KafkaMessagePublisher(IOptions<KafkaOptions> options)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            ClientId = "server-monitor",

            Acks = Acks.All,
            EnableIdempotence = true,

            LingerMs = 5,
            CompressionType = CompressionType.Lz4,

            MessageTimeoutMs = 10_000,
        };

        _producer = new ProducerBuilder<string, byte[]>(config).Build();
    }

    public async Task PublishAsync(string topic, string key, byte[] value, CancellationToken ct)
    {
        try
        {
            await _producer.ProduceAsync(topic, new Message<string, byte[]> { Key = key, Value = value }, ct);
        }
        catch (ProduceException<string, byte[]> ex)
        {
            throw new MessagePublishException($"Kafka did not accept the message: {ex.Error.Reason}", ex);
        }
        catch (KafkaException ex)
        {
            throw new MessagePublishException($"Kafka error: {ex.Error.Reason}", ex);
        }
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}
