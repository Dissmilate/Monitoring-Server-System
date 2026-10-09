using ServerMonitor.Contracts.Servers;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Servers;

internal static class ServerMappings
{
    public static ServerDto ToDto(this Server server) => new(
        server.Id,
        server.Name,
        server.Status.ToString(),
        server.Hostname,
        server.OperatingSystem,
        server.AgentVersion,
        server.CreatedAt,
        server.LastSeenAt,
        server.ActiveAgentKey?.Prefix);
}
