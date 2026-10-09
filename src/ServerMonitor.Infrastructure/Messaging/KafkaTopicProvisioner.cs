using System.Globalization;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ServerMonitor.Infrastructure.Messaging;

public sealed partial class KafkaTopicProvisioner(IOptions<KafkaOptions> options, ILogger<KafkaTopicProvisioner> logger)
{
    private static readonly TimeSpan TelemetryRetention = TimeSpan.FromDays(1);

    private static readonly TimeSpan SignalRetention = TimeSpan.FromHours(1);

    public async Task EnsureTopicsAsync(CancellationToken ct)
    {
        var settings = options.Value;
        TopicSpecification[] topics =
        [
            new()
            {
                Name = KafkaTopics.Metrics,
                NumPartitions = settings.TelemetryPartitions,
                ReplicationFactor = settings.ReplicationFactor,

                Configs = new() { ["retention.ms"] = TelemetryRetention.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) },
            },
            new()
            {
                Name = KafkaTopics.MetricsDeadLetter,
                NumPartitions = 1,
                ReplicationFactor = settings.ReplicationFactor,
            },
            new()
            {
                Name = KafkaTopics.Logs,
                NumPartitions = settings.TelemetryPartitions,
                ReplicationFactor = settings.ReplicationFactor,
                Configs = new() { ["retention.ms"] = TelemetryRetention.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) },
            },
            new()
            {
                Name = KafkaTopics.LogsDeadLetter,
                NumPartitions = 1,
                ReplicationFactor = settings.ReplicationFactor,
            },
            new()
            {
                Name = KafkaTopics.AgentCommands,
                NumPartitions = settings.TelemetryPartitions,
                ReplicationFactor = settings.ReplicationFactor,
                Configs = new() { ["retention.ms"] = SignalRetention.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) },
            },
            new()
            {
                Name = KafkaTopics.MonitorEvents,
                NumPartitions = settings.TelemetryPartitions,
                ReplicationFactor = settings.ReplicationFactor,
                Configs = new() { ["retention.ms"] = SignalRetention.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) },
            },
        ];

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = settings.BootstrapServers }).Build();
        try
        {
            await admin.CreateTopicsAsync(topics);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
        }

        ct.ThrowIfCancellationRequested();
        LogTopicsReady(logger, string.Join(", ", topics.Select(t => t.Name)));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Kafka topics ready: {Topics}")]
    private static partial void LogTopicsReady(ILogger logger, string topics);
}
