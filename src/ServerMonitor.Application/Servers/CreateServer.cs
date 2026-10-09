using ServerMonitor.Application.Abstractions;
using ServerMonitor.Contracts.Servers;
using ServerMonitor.Domain.Common;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Servers;

public sealed class CreateServerHandler(IServerRepository servers, IUnitOfWork unitOfWork, TimeProvider time)
{
    public async Task<Result<ServerDto>> HandleAsync(CreateServerRequest request, CancellationToken ct)
    {
        var created = Server.Create(request.Name, time.GetUtcNow());
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        var server = created.Value;
        if (await servers.NameExistsAsync(server.Name, excludeId: null, ct))
        {
            return ServerErrors.NameTaken(server.Name);
        }

        servers.Add(server);
        await unitOfWork.SaveChangesAsync(ct);
        return server.ToDto();
    }
}
