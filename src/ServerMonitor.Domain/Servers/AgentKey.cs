namespace ServerMonitor.Domain.Servers;

public sealed class AgentKey
{
    private AgentKey()
    {
    }

    public Guid Id { get; private set; }

    public Guid ServerId { get; private set; }

    public string Prefix { get; private set; } = null!;

    public string KeyHash { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive => RevokedAt is null;

    internal static AgentKey Create(Guid serverId, string prefix, string keyHash, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(now),
            ServerId = serverId,
            Prefix = prefix,
            KeyHash = keyHash,
            CreatedAt = now,
        };

    internal void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
