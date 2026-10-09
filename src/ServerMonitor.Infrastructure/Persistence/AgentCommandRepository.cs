using Microsoft.EntityFrameworkCore;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain.Agents;

namespace ServerMonitor.Infrastructure.Persistence;

internal sealed class AgentCommandRepository(MonitorDbContext db) : IAgentCommandRepository
{
    public Task<AgentCommand?> GetAsync(Guid id, CancellationToken ct) =>
        db.Set<AgentCommand>().FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<Guid>> ListDeliverableIdsAsync(Guid serverId, DateTimeOffset now, CancellationToken ct) =>
        await db.Set<AgentCommand>()
            .Where(c => c.ServerId == serverId
                && (c.Status == AgentCommandStatus.Pending || c.Status == AgentCommandStatus.Delivered)
                && c.ExpiresAt > now)
            .OrderBy(c => c.CreatedAt)
            .Select(c => c.Id)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AgentCommand>> ListRecentAsync(Guid serverId, int limit, CancellationToken ct) =>
        await db.Set<AgentCommand>()
            .AsNoTracking()
            .Where(c => c.ServerId == serverId)
            .OrderByDescending(c => c.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

    public void Add(AgentCommand command) => db.Set<AgentCommand>().Add(command);
}
