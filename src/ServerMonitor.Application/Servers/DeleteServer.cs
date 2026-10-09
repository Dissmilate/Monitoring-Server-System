using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain.Common;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Servers;

public sealed class DeleteServerHandler(IServerRepository servers, IUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(Guid id, CancellationToken ct)
    {
        var server = await servers.GetByIdAsync(id, ct);
        if (server is null)
        {
            return ServerErrors.NotFound(id);
        }

        servers.Remove(server);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
