using Google.Protobuf;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Application.Logs;
using ServerMonitor.Contracts.Logs;
using ServerMonitor.Infrastructure.Messaging;
using ServerMonitor.Protocol.Telemetry.V1;
using ServerMonitor.Protocol.V1;
using ServerMonitor.Workers.Messaging;

namespace ServerMonitor.Workers.Logs;

public sealed class LogIndexerService(
    KafkaConsumerFactory consumers,
    ILogWriter writer,
    IMessagePublisher publisher,
    ILogger<LogIndexerService> logger)
    : BatchConsumerService<LogBatchEnvelope>(consumers, publisher, logger)
{
    protected override string Topic => KafkaTopics.Logs;

    protected override string DeadLetterTopic => KafkaTopics.LogsDeadLetter;

    protected override string ConsumerGroup => "log-indexer";

    protected override MessageParser<LogBatchEnvelope> Parser => LogBatchEnvelope.Parser;

    protected override bool IsValid(LogBatchEnvelope message) => Guid.TryParse(message.ServerId, out _);

    protected override Task StoreAsync(IReadOnlyList<LogBatchEnvelope> messages, CancellationToken ct) =>
        writer.WriteAsync(
            [.. messages.SelectMany(m => m.Batch.Entries.Select(e => new LogRecord(
                e.Id,
                Guid.Parse(m.ServerId),
                e.Time.ToDateTimeOffset(),
                ToName(e.Level),
                e.Source,
                e.Message)))],
            ct);

    internal static string ToName(LogSeverity level) => level switch
    {
        LogSeverity.Trace => LogLevels.Trace,
        LogSeverity.Debug => LogLevels.Debug,
        LogSeverity.Warning => LogLevels.Warning,
        LogSeverity.Error => LogLevels.Error,
        LogSeverity.Critical => LogLevels.Critical,
        _ => LogLevels.Information,
    };
}
