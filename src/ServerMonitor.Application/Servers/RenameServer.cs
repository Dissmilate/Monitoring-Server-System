using ServerMonitor.Application.Abstractions;
using ServerMonitor.Contracts.Servers;
using ServerMonitor.Domain.Common;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Servers;

public sealed class RenameServerHandler(IServerRepository servers, IUnitOfWork unitOfWork)
{
    public async Task<Result<ServerDto>> HandleAsync(Guid id, RenameServerRequest request, CancellationToken ct)
    {
        var server = await servers.GetByIdAsync(id, ct);
        if (server is null)
        {
            return ServerErrors.NotFound(id);
        }

        var renamed = server.Rename(request.Name);
        if (!renamed.IsSuccess)
        {
            return renamed.Error;
        }

        if (await servers.NameExistsAsync(server.Name, excludeId: id, ct))
        {
            return ServerErrors.NameTaken(server.Name);
        }

        await unitOfWork.SaveChangesAsync(ct);
        return server.ToDto();
    }
}
