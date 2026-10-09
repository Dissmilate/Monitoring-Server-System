using ServerMonitor.Domain.Common;

namespace ServerMonitor.Domain.Servers;

public sealed class Server
{
    public const int MaxNameLength = 100;
    public const int DefaultMetricsIntervalSeconds = 10;
    public const int MinMetricsIntervalSeconds = 5;
    public const int MaxMetricsIntervalSeconds = 3600;

    private readonly List<EnrollmentToken> _enrollmentTokens = [];
    private readonly List<AgentKey> _agentKeys = [];

    private Server()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = null!;

    public ServerStatus Status { get; private set; }

    public string? Hostname { get; private set; }

    public string? OperatingSystem { get; private set; }

    public string? AgentVersion { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? LastSeenAt { get; private set; }

    public Guid? ConnectionId { get; private set; }

    public int Version { get; private set; }

    public long ConfigVersion { get; private set; }

    public int MetricsIntervalSeconds { get; private set; }

    public IReadOnlyCollection<EnrollmentToken> EnrollmentTokens => _enrollmentTokens;

    public IReadOnlyCollection<AgentKey> AgentKeys => _agentKeys;

    public AgentKey? ActiveAgentKey => _agentKeys.FirstOrDefault(k => k.IsActive);

    public static Result<Server> Create(string name, DateTimeOffset now)
    {
        if (ValidateName(name) is { } error)
        {
            return error;
        }

        return new Server
        {
            Id = Guid.CreateVersion7(now),
            Name = name.Trim(),
            Status = ServerStatus.Pending,
            CreatedAt = now,
            ConfigVersion = 1,
            MetricsIntervalSeconds = DefaultMetricsIntervalSeconds,
        };
    }

    public Result Rename(string name)
    {
        if (ValidateName(name) is { } error)
        {
            return error;
        }

        Name = name.Trim();
        Touch();
        return Result.Success();
    }

    public EnrollmentToken IssueEnrollmentToken(string tokenHash, DateTimeOffset now, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        foreach (var active in _enrollmentTokens.Where(t => t.IsActive(now)))
        {
            active.Revoke(now);
        }

        var token = EnrollmentToken.Create(Id, tokenHash, now, lifetime);
        _enrollmentTokens.Add(token);
        Touch();
        return token;
    }

    public Result<AgentKey> Enroll(
        string enrollmentTokenHash,
        AgentHost host,
        string keyPrefix,
        string keyHash,
        DateTimeOffset now)
    {
        var token = _enrollmentTokens.FirstOrDefault(t => t.TokenHash == enrollmentTokenHash);
        if (token is null || !token.IsActive(now))
        {
            return ServerErrors.InvalidEnrollmentToken;
        }

        token.MarkUsed(now);
        RevokeAgentKeys(now);

        var key = AgentKey.Create(Id, keyPrefix, keyHash, now);
        _agentKeys.Add(key);

        var normalized = host.Normalize();
        Hostname = normalized.Hostname;
        OperatingSystem = normalized.OperatingSystem;
        AgentVersion = normalized.AgentVersion;

        Status = ServerStatus.Offline;
        LastSeenAt = now;
        ConnectionId = null;

        Touch();
        return key;
    }

    public void RevokeAgentKeys(DateTimeOffset now)
    {
        foreach (var key in _agentKeys.Where(k => k.IsActive))
        {
            key.Revoke(now);
        }

        Touch();
    }

    public Result<bool> UpdateConfiguration(int metricsIntervalSeconds)
    {
        if (metricsIntervalSeconds is < MinMetricsIntervalSeconds or > MaxMetricsIntervalSeconds)
        {
            return ServerErrors.InvalidMetricsInterval;
        }

        if (metricsIntervalSeconds == MetricsIntervalSeconds)
        {
            return false;
        }

        MetricsIntervalSeconds = metricsIntervalSeconds;
        ConfigVersion++;
        Touch();
        return true;
    }

    private void Touch() => Version++;

    private static Error? ValidateName(string name) => name switch
    {
        _ when string.IsNullOrWhiteSpace(name) => ServerErrors.NameRequired,
        _ when name.Trim().Length > MaxNameLength => ServerErrors.NameTooLong,
        _ => null,
    };
}
