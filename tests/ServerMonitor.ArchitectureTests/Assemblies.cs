using System.Reflection;
using ServerMonitor.Agent;
using ServerMonitor.Api;
using ServerMonitor.Application;
using ServerMonitor.Contracts.Auth;
using ServerMonitor.Domain;
using ServerMonitor.Gateway;
using ServerMonitor.Infrastructure;
using ServerMonitor.Migrator;
using ServerMonitor.Protocol.V1;
using ServerMonitor.Workers;

namespace ServerMonitor.ArchitectureTests;

internal static class Assemblies
{
    public static readonly Assembly Domain = typeof(IDomainMarker).Assembly;
    public static readonly Assembly Contracts = typeof(Roles).Assembly;
    public static readonly Assembly Protocol = typeof(AgentGateway).Assembly;
    public static readonly Assembly Application = typeof(IApplicationMarker).Assembly;
    public static readonly Assembly Infrastructure = typeof(IInfrastructureMarker).Assembly;
    public static readonly Assembly Api = typeof(IApiMarker).Assembly;
    public static readonly Assembly Gateway = typeof(IGatewayMarker).Assembly;
    public static readonly Assembly Workers = typeof(IWorkersMarker).Assembly;
    public static readonly Assembly Migrator = typeof(IMigratorMarker).Assembly;
    public static readonly Assembly Agent = typeof(IAgentMarker).Assembly;
    public static readonly Assembly Dashboard = typeof(ServerMonitor.Dashboard.App).Assembly;

    public static readonly Assembly[] Hosts = [Api, Gateway, Workers, Migrator, Agent];

    public static string Name(this Assembly assembly) => assembly.GetName().Name!;

    public static IReadOnlyList<string> ReferencedNames(this Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(a => a.Name!)];

    public static IReadOnlyList<string> ReferencedSolutionNames(this Assembly assembly) =>
        [.. assembly.ReferencedNames().Where(n => n.StartsWith("ServerMonitor.", StringComparison.Ordinal))];
}
