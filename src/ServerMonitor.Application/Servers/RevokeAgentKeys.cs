using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain.Common;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Servers;

public sealed class RevokeAgentKeysHandler(IServerRepository servers, IUnitOfWork unitOfWork, TimeProvider time)
{
    public async Task<Result> HandleAsync(Guid serverId, CancellationToken ct)
    {
        var server = await servers.GetByIdAsync(serverId, ct);
        if (server is null)
        {
            return ServerErrors.NotFound(serverId);
        }

        server.RevokeAgentKeys(time.GetUtcNow());
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
