namespace ServerMonitor.Domain.Servers;

public sealed class EnrollmentToken
{
    private EnrollmentToken()
    {
    }

    public Guid Id { get; private set; }

    public Guid ServerId { get; private set; }

    public string TokenHash { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive(DateTimeOffset now) => UsedAt is null && RevokedAt is null && now < ExpiresAt;

    internal static EnrollmentToken Create(Guid serverId, string tokenHash, DateTimeOffset now, TimeSpan lifetime) =>
        new()
        {
            Id = Guid.CreateVersion7(now),
            ServerId = serverId,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };

    internal void MarkUsed(DateTimeOffset now) => UsedAt ??= now;

    internal void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
