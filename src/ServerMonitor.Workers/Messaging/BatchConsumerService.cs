using System.Diagnostics;
using Confluent.Kafka;
using Google.Protobuf;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Infrastructure.Messaging;

namespace ServerMonitor.Workers.Messaging;

public abstract class BatchConsumerService<TMessage>(
    KafkaConsumerFactory consumers,
    IMessagePublisher publisher,
    ILogger logger)
    : BackgroundService
    where TMessage : IMessage<TMessage>
{
    private const int MaxBatchMessages = 500;
    private static readonly TimeSpan MaxBatchWait = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    protected abstract string Topic { get; }

    protected abstract string DeadLetterTopic { get; }

    protected abstract string ConsumerGroup { get; }

    protected abstract MessageParser<TMessage> Parser { get; }

    protected virtual bool IsValid(TMessage message) => true;

    protected abstract Task StoreAsync(IReadOnlyList<TMessage> messages, CancellationToken ct);

    // Consume() is blocking.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() => RunAsync(stoppingToken), stoppingToken);

    private async Task RunAsync(CancellationToken ct)
    {
        using var consumer = consumers.Create(ConsumerGroup);
        consumer.Subscribe(Topic);
        ConsumerLog.Started(logger, Topic);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                List<ConsumeResult<string, byte[]>> results;
                try
                {
                    results = ConsumeBatch(consumer, ct);
                }
                catch (ConsumeException ex) when (!ex.Error.IsFatal)
                {
                    ConsumerLog.ConsumeFailed(logger, Topic, ex.Error.Reason);
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                var messages = await DecodeAsync(results, ct);
                if (messages.Count > 0)
                {
                    await RetryAsync(() => StoreAsync(messages, ct), ct);
                }

                StoreOffsets(consumer, results);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            consumer.Close();
        }
    }

    private static List<ConsumeResult<string, byte[]>> ConsumeBatch(IConsumer<string, byte[]> consumer, CancellationToken ct)
    {
        var results = new List<ConsumeResult<string, byte[]>> { consumer.Consume(ct) };
        var started = Stopwatch.GetTimestamp();

        while (results.Count < MaxBatchMessages)
        {
            var remaining = MaxBatchWait - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero || consumer.Consume(remaining) is not { } next)
            {
                break;
            }

            results.Add(next);
        }

        return results;
    }

    private async Task<List<TMessage>> DecodeAsync(List<ConsumeResult<string, byte[]>> results, CancellationToken ct)
    {
        var messages = new List<TMessage>(results.Count);
        foreach (var result in results)
        {
            TMessage? message;
            try
            {
                message = Parser.ParseFrom(result.Message.Value);
            }
            catch (InvalidProtocolBufferException)
            {
                message = default;
            }

            if (message is not null && IsValid(message))
            {
                messages.Add(message);
                continue;
            }

            ConsumerLog.PoisonMessage(logger, result.TopicPartitionOffset.ToString(), DeadLetterTopic);
            await RetryAsync(
                () => publisher.PublishAsync(DeadLetterTopic, result.Message.Key ?? string.Empty, result.Message.Value, ct),
                ct);
        }

        return messages;
    }

    // Do not move on (and do not store the offsets) until the batch is saved.
    private async Task RetryAsync(Func<Task> action, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ConsumerLog.ProcessingFailed(logger, ex, Topic, delay);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
            }
        }
    }

    private void StoreOffsets(IConsumer<string, byte[]> consumer, List<ConsumeResult<string, byte[]>> results)
    {
        foreach (var partition in results.GroupBy(r => r.TopicPartition))
        {
            try
            {
                consumer.StoreOffset(new TopicPartitionOffset(partition.Key, partition.Max(r => r.Offset.Value) + 1));
            }
            catch (KafkaException ex)
            {
                // The partition was taken away meanwhile; its messages are read again, and the stores are idempotent.
                ConsumerLog.OffsetNotStored(logger, partition.Key.ToString(), ex.Error.Reason);
            }
        }
    }
}

internal static partial class ConsumerLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Consuming {Topic}")]
    public static partial void Started(ILogger logger, string topic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consuming {Topic} failed: {Reason}; retrying")]
    public static partial void ConsumeFailed(ILogger logger, string topic, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Invalid message at {Position}; moved to {DeadLetterTopic}")]
    public static partial void PoisonMessage(ILogger logger, string position, string deadLetterTopic);

    [LoggerMessage(Level = LogLevel.Error, Message = "Processing messages from {Topic} failed; retrying in {Delay}")]
    public static partial void ProcessingFailed(ILogger logger, Exception exception, string topic, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Offset for {Partition} not stored: {Reason}")]
    public static partial void OffsetNotStored(ILogger logger, string partition, string reason);
}
