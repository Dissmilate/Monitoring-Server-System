using ServerMonitor.Application.Abstractions;
using ServerMonitor.Contracts.Servers;

namespace ServerMonitor.Application.Servers;

public sealed class ListServersHandler(IServerRepository servers)
{
    public async Task<IReadOnlyList<ServerDto>> HandleAsync(CancellationToken ct)
    {
        var list = await servers.ListAsync(ct);
        return [.. list.Select(s => s.ToDto())];
    }
}
