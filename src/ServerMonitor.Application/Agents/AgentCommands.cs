using ServerMonitor.Application.Abstractions;
using ServerMonitor.Contracts.Agents;
using ServerMonitor.Domain.Agents;
using ServerMonitor.Domain.Common;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Agents;

internal static class AgentCommandMappings
{
    public static AgentCommandDto ToDto(this AgentCommand command) => new(
        command.Id,
        command.Type.ToString(),
        command.Status.ToString(),
        command.CreatedAt,
        command.ExpiresAt,
        command.DeliveredAt,
        command.CompletedAt,
        command.ResultMessage);
}

public sealed class RequestAgentCommandHandler(
    IServerRepository servers,
    IAgentCommandRepository commands,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    public async Task<Result<AgentCommandDto>> HandleAsync(
        Guid serverId, RequestAgentCommandRequest request, CancellationToken ct)
    {
        if (!string.Equals(request.Type, nameof(AgentCommandType.CollectNow), StringComparison.OrdinalIgnoreCase))
        {
            return AgentCommandErrors.UnsupportedType;
        }

        var server = await servers.GetByIdAsync(serverId, ct);
        if (server is null)
        {
            return ServerErrors.NotFound(serverId);
        }

        if (server.ActiveAgentKey is null)
        {
            return ServerErrors.NotEnrolled;
        }

        var command = AgentCommand.Create(serverId, AgentCommandType.CollectNow, time.GetUtcNow());
        commands.Add(command);
        await unitOfWork.SaveChangesAsync(ct);
        return command.ToDto();
    }
}

public sealed class ListAgentCommandsHandler(IServerRepository servers, IAgentCommandRepository commands)
{
    public const int MaxLimit = 100;

    public async Task<Result<IReadOnlyList<AgentCommandDto>>> HandleAsync(Guid serverId, int? limit, CancellationToken ct)
    {
        if (!await servers.ExistsAsync(serverId, ct))
        {
            return ServerErrors.NotFound(serverId);
        }

        var recent = await commands.ListRecentAsync(serverId, Math.Clamp(limit ?? 20, 1, MaxLimit), ct);
        return Result<IReadOnlyList<AgentCommandDto>>.Success([.. recent.Select(c => c.ToDto())]);
    }
}

public sealed class GetAgentCommandHandler(IAgentCommandRepository commands)
{
    public async Task<Result<AgentCommandDto>> HandleAsync(Guid serverId, Guid commandId, CancellationToken ct)
    {
        var command = await commands.GetAsync(commandId, ct);
        return command is null || command.ServerId != serverId
            ? AgentCommandErrors.NotFound(commandId)
            : command.ToDto();
    }
}
