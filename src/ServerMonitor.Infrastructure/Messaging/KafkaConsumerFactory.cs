using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace ServerMonitor.Infrastructure.Messaging;

public sealed class KafkaConsumerFactory(IOptions<KafkaOptions> options)
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(10);

    // The caller stores an offset (StoreOffset) only after the data is saved; stored offsets are committed
    // in the background. A synchronous Commit() can hang while the group coordinator is unreachable.
    public IConsumer<string, byte[]> Create(string groupId) =>
        new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            GroupId = groupId,
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,
            AutoCommitIntervalMs = 1000,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
        }).Build();

    // Every instance reads all partitions from the end. No consumer group: after Kafka was unreachable
    // a group member can get stuck in a rebalance, while an assigned consumer just reconnects.
    // Throws KafkaException while the broker or the topics are not available yet.
    public IConsumer<string, byte[]> CreateBroadcast(params IReadOnlyList<string> topics)
    {
        var partitions = GetPartitions(topics);
        var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            GroupId = $"broadcast-{Guid.NewGuid():N}",
            EnableAutoCommit = false,
        }).Build();

        consumer.Assign(partitions.Select(p => new TopicPartitionOffset(p, Offset.End)));
        return consumer;
    }

    private List<TopicPartition> GetPartitions(IReadOnlyList<string> topics)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.Value.BootstrapServers }).Build();
        var partitions = new List<TopicPartition>();
        foreach (var topic in topics)
        {
            var metadata = admin.GetMetadata(topic, MetadataTimeout).Topics.Single();
            if (metadata.Error.IsError || metadata.Partitions.Count == 0)
            {
                throw new KafkaException(metadata.Error.IsError ? metadata.Error : new Error(ErrorCode.UnknownTopicOrPart));
            }

            partitions.AddRange(metadata.Partitions.Select(p => new TopicPartition(topic, p.PartitionId)));
        }

        return partitions;
    }
}
