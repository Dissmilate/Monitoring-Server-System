using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Application.Tests.Fakes;

internal sealed class InMemoryServerRepository : IServerRepository, IUnitOfWork
{
    public List<Server> Servers { get; } = [];

    public int SaveCount { get; private set; }

    public bool FailNextSaveWithConcurrencyConflict { get; set; }

    public Task<Server?> GetByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Servers.FirstOrDefault(s => s.Id == id));

    public Task<Server?> GetByEnrollmentTokenHashAsync(string tokenHash, CancellationToken ct) =>
        Task.FromResult(Servers.FirstOrDefault(s => s.EnrollmentTokens.Any(t => t.TokenHash == tokenHash)));

    public Task<AgentKey?> FindAgentKeyAsync(string prefix, CancellationToken ct) =>
        Task.FromResult(Servers.SelectMany(s => s.AgentKeys).FirstOrDefault(k => k.Prefix == prefix));

    public Task<IReadOnlyList<Server>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Server>>([.. Servers.OrderBy(s => s.Name, StringComparer.Ordinal)]);

    public Task<bool> IsAgentKeyActiveAsync(Guid keyId, CancellationToken ct) =>
        Task.FromResult(Servers.SelectMany(s => s.AgentKeys).Any(k => k.Id == keyId && k.IsActive));

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Servers.Any(s => s.Id == id));

    public Task<bool> NameExistsAsync(string name, Guid? excludeId, CancellationToken ct) =>
        Task.FromResult(Servers.Any(s => s.Name == name && s.Id != excludeId));

    public void Add(Server server) => Servers.Add(server);

    public void Remove(Server server) => Servers.Remove(server);

    public Task SaveChangesAsync(CancellationToken ct)
    {
        if (FailNextSaveWithConcurrencyConflict)
        {
            FailNextSaveWithConcurrencyConflict = false;
            throw new ConcurrencyConflictException("Simulated conflict.");
        }

        SaveCount++;
        return Task.CompletedTask;
    }
}
