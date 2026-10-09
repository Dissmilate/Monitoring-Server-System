namespace ServerMonitor.Contracts.Agents;

public sealed record AgentConfigurationDto(long Version, int MetricsIntervalSeconds);

public sealed record UpdateAgentConfigurationRequest(int MetricsIntervalSeconds);

public sealed record RequestAgentCommandRequest(string Type);

public sealed record AgentCommandDto(
    Guid Id,
    string Type,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? CompletedAt,
    string? ResultMessage);
