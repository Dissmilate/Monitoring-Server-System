using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Abstractions;

public interface IServerRepository
{
    Task<Server?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<Server?> GetByEnrollmentTokenHashAsync(string tokenHash, CancellationToken ct);

    Task<AgentKey?> FindAgentKeyAsync(string prefix, CancellationToken ct);

    Task<bool> IsAgentKeyActiveAsync(Guid keyId, CancellationToken ct);

    Task<IReadOnlyList<Server>> ListAsync(CancellationToken ct);

    Task<bool> ExistsAsync(Guid id, CancellationToken ct);

    Task<bool> NameExistsAsync(string name, Guid? excludeId, CancellationToken ct);

    void Add(Server server);

    void Remove(Server server);
}
