using Google.Protobuf;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Application.Metrics;
using ServerMonitor.Infrastructure.Messaging;
using ServerMonitor.Protocol.Telemetry.V1;
using ServerMonitor.Workers.Messaging;

namespace ServerMonitor.Workers.Metrics;

public sealed class MetricsWriterService(
    KafkaConsumerFactory consumers,
    IMetricWriter writer,
    IMessagePublisher publisher,
    ILogger<MetricsWriterService> logger)
    : BatchConsumerService<MetricBatchEnvelope>(consumers, publisher, logger)
{
    public const string Group = "metrics-writer";

    protected override string Topic => KafkaTopics.Metrics;

    protected override string DeadLetterTopic => KafkaTopics.MetricsDeadLetter;

    protected override string ConsumerGroup => Group;

    protected override MessageParser<MetricBatchEnvelope> Parser => MetricBatchEnvelope.Parser;

    protected override bool IsValid(MetricBatchEnvelope message) => Guid.TryParse(message.ServerId, out _);

    protected override Task StoreAsync(IReadOnlyList<MetricBatchEnvelope> messages, CancellationToken ct) =>
        writer.WriteAsync(MetricBatchMapper.ToWriteBatch(messages), ct);
}
