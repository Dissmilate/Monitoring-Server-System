using ServerMonitor.Application.Abstractions;
using ServerMonitor.Contracts.Servers;
using ServerMonitor.Domain.Common;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Servers;

public sealed class GetServerHandler(IServerRepository servers)
{
    public async Task<Result<ServerDto>> HandleAsync(Guid id, CancellationToken ct)
    {
        var server = await servers.GetByIdAsync(id, ct);
        return server is null ? ServerErrors.NotFound(id) : server.ToDto();
    }
}
