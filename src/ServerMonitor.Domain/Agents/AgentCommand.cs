using ServerMonitor.Domain.Common;

namespace ServerMonitor.Domain.Agents;

public enum AgentCommandType
{
    ApplyConfig,

    CollectNow,
}

public enum AgentCommandStatus
{
    Pending,
    Delivered,
    Succeeded,
    Failed,
    Expired,
}

public sealed class AgentCommand
{
    public const int MaxResultMessageLength = 1000;

    private AgentCommand()
    {
    }

    public Guid Id { get; private set; }

    public Guid ServerId { get; private set; }

    public AgentCommandType Type { get; private set; }

    public AgentCommandStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? ResultMessage { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public bool IsCompleted => Status is AgentCommandStatus.Succeeded or AgentCommandStatus.Failed or AgentCommandStatus.Expired;

    public static AgentCommand Create(Guid serverId, AgentCommandType type, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        ServerId = serverId,
        Type = type,
        Status = AgentCommandStatus.Pending,
        CreatedAt = now,
        ExpiresAt = now + Lifetime(type),
    };

    public bool IsDeliverable(DateTimeOffset now) =>
        Status is AgentCommandStatus.Pending or AgentCommandStatus.Delivered && now < ExpiresAt;

    public void MarkDelivered(DateTimeOffset now)
    {
        if (Status == AgentCommandStatus.Pending)
        {
            Status = AgentCommandStatus.Delivered;
        }

        DeliveredAt = now;
    }

    public Result Complete(bool succeeded, string? message, DateTimeOffset now)
    {
        if (IsCompleted)
        {
            return AgentCommandErrors.AlreadyCompleted(Id);
        }

        Status = succeeded ? AgentCommandStatus.Succeeded : AgentCommandStatus.Failed;
        CompletedAt = now;
        ResultMessage = message is { Length: > MaxResultMessageLength } ? message[..MaxResultMessageLength] : message;
        return Result.Success();
    }

    private static TimeSpan Lifetime(AgentCommandType type) => type switch
    {
        AgentCommandType.CollectNow => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromHours(24),
    };
}

public static class AgentCommandErrors
{
    public static readonly Error UnsupportedType =
        Error.Validation("AgentCommand.UnsupportedType", "Supported command types: CollectNow.");

    public static Error NotFound(Guid id) =>
        Error.NotFound("AgentCommand.NotFound", $"Command '{id}' was not found.");

    public static Error AlreadyCompleted(Guid id) =>
        Error.Conflict("AgentCommand.AlreadyCompleted", $"Command '{id}' is already completed.");
}
