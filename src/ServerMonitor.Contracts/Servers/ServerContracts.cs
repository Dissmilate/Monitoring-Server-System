namespace ServerMonitor.Contracts.Servers;

public sealed record ServerDto(
    Guid Id,
    string Name,
    string Status,
    string? Hostname,
    string? OperatingSystem,
    string? AgentVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    string? AgentKeyPrefix);

public sealed record CreateServerRequest(string Name);

public sealed record RenameServerRequest(string Name);

public sealed record EnrollmentTokenDto(string Token, DateTimeOffset ExpiresAt);
