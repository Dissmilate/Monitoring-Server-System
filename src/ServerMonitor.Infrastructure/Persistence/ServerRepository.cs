using Microsoft.EntityFrameworkCore;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Infrastructure.Persistence;

internal sealed class ServerRepository(MonitorDbContext db) : IServerRepository
{
    public Task<Server?> GetByIdAsync(Guid id, CancellationToken ct) =>
        Aggregates().FirstOrDefaultAsync(s => s.Id == id, ct);

    public Task<Server?> GetByEnrollmentTokenHashAsync(string tokenHash, CancellationToken ct) =>
        Aggregates().FirstOrDefaultAsync(s => s.EnrollmentTokens.Any(t => t.TokenHash == tokenHash), ct);

    public Task<AgentKey?> FindAgentKeyAsync(string prefix, CancellationToken ct) =>
        db.Set<AgentKey>()
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Prefix == prefix, ct);

    public Task<bool> IsAgentKeyActiveAsync(Guid keyId, CancellationToken ct) =>
        db.Set<AgentKey>().AnyAsync(k => k.Id == keyId && k.RevokedAt == null, ct);

    public async Task<IReadOnlyList<Server>> ListAsync(CancellationToken ct) =>
        await db.Servers
            .AsNoTracking()
            .Include(s => s.AgentKeys.Where(k => k.RevokedAt == null))
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) =>
        db.Servers.AnyAsync(s => s.Id == id, ct);

    public Task<bool> NameExistsAsync(string name, Guid? excludeId, CancellationToken ct) =>
        db.Servers.AnyAsync(s => s.Name == name && (excludeId == null || s.Id != excludeId), ct);

    public void Add(Server server) => db.Servers.Add(server);

    public void Remove(Server server) => db.Servers.Remove(server);

    private IQueryable<Server> Aggregates() =>
        db.Servers
            .Include(s => s.EnrollmentTokens)
            .Include(s => s.AgentKeys)
            .AsSplitQuery();
}
