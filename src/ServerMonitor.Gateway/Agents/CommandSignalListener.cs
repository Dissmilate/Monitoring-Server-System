using Confluent.Kafka;
using Google.Protobuf;
using ServerMonitor.Infrastructure.Messaging;
using ServerMonitor.Protocol.Messaging.V1;

namespace ServerMonitor.Gateway.Agents;

internal sealed partial class CommandSignalListener(
    KafkaConsumerFactory consumers,
    AgentConnectionRegistry registry,
    ILogger<CommandSignalListener> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() => Run(stoppingToken), stoppingToken);

    private void Run(CancellationToken ct)
    {
        using var consumer = Connect(ct);
        if (consumer is null)
        {
            return;
        }

        // Signals sent before the partitions were assigned are lost: re-check the database.
        registry.ResyncAll();
        LogListening(logger, consumer.Assignment.Count);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]>? result;
                try
                {
                    result = consumer.Consume(TimeSpan.FromSeconds(1));
                }
                catch (ConsumeException ex) when (!ex.Error.IsFatal)
                {
                    LogConsumeFailed(logger, ex.Error.Reason);
                    ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                    continue;
                }

                if (result is null)
                {
                    continue;
                }

                try
                {
                    var signal = AgentCommandSignal.Parser.ParseFrom(result.Message.Value);
                    if (Guid.TryParse(signal.ServerId, out var serverId) && Guid.TryParse(signal.CommandId, out var commandId))
                    {
                        registry.TrySignal(serverId, commandId);
                    }
                }
                catch (InvalidProtocolBufferException ex)
                {
                    LogInvalidSignal(logger, ex, result.TopicPartitionOffset.ToString());
                }
            }
        }
        finally
        {
            consumer.Close();
        }
    }

    private IConsumer<string, byte[]>? Connect(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                return consumers.CreateBroadcast(KafkaTopics.AgentCommands);
            }
            catch (KafkaException ex)
            {
                LogConsumeFailed(logger, ex.Error.Reason);
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            }
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening for agent commands on {Partitions} partitions")]
    private static partial void LogListening(ILogger logger, int partitions);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consuming command signals failed: {Reason}; retrying")]
    private static partial void LogConsumeFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring an undecodable command signal at {Position}")]
    private static partial void LogInvalidSignal(ILogger logger, Exception exception, string position);
}
