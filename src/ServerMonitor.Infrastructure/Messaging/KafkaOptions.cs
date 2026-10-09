namespace ServerMonitor.Infrastructure.Messaging;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";

    public short ReplicationFactor { get; set; } = 1;

    public int TelemetryPartitions { get; set; } = 6;
}

public static class KafkaTopics
{
    public const string Metrics = "telemetry.metrics";

    public const string MetricsDeadLetter = "telemetry.metrics.dlq";

    public const string Logs = "telemetry.logs";

    public const string LogsDeadLetter = "telemetry.logs.dlq";

    public const string AgentCommands = "agent.commands";

    public const string MonitorEvents = "monitor.events";
}
