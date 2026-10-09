using ServerMonitor.Application.Abstractions;
using ServerMonitor.Contracts.Agents;
using ServerMonitor.Domain.Agents;
using ServerMonitor.Domain.Common;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Agents;

public sealed class GetAgentConfigurationHandler(IServerRepository servers)
{
    public async Task<Result<AgentConfigurationDto>> HandleAsync(Guid serverId, CancellationToken ct)
    {
        var server = await servers.GetByIdAsync(serverId, ct);
        return server is null
            ? ServerErrors.NotFound(serverId)
            : new AgentConfigurationDto(server.ConfigVersion, server.MetricsIntervalSeconds);
    }
}

// ApplyConfig is queued in the same transaction (outbox).
public sealed class UpdateAgentConfigurationHandler(
    IServerRepository servers,
    IAgentCommandRepository commands,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    public async Task<Result<AgentConfigurationDto>> HandleAsync(
        Guid serverId, UpdateAgentConfigurationRequest request, CancellationToken ct)
    {
        var server = await servers.GetByIdAsync(serverId, ct);
        if (server is null)
        {
            return ServerErrors.NotFound(serverId);
        }

        var updated = server.UpdateConfiguration(request.MetricsIntervalSeconds);
        if (!updated.IsSuccess)
        {
            return updated.Error;
        }

        if (updated.Value)
        {
            commands.Add(AgentCommand.Create(server.Id, AgentCommandType.ApplyConfig, time.GetUtcNow()));
            await unitOfWork.SaveChangesAsync(ct);
        }

        return new AgentConfigurationDto(server.ConfigVersion, server.MetricsIntervalSeconds);
    }
}
