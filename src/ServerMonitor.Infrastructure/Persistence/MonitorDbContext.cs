using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain.Servers;
using ServerMonitor.Infrastructure.Identity;
using ServerMonitor.Infrastructure.Persistence.Configurations;

namespace ServerMonitor.Infrastructure.Persistence;

public sealed class MonitorDbContext(DbContextOptions<MonitorDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IUnitOfWork, IDataProtectionKeyContext
{
    public DbSet<Server> Servers => Set<Server>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    async Task IUnitOfWork.SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException("The data was modified by another request.", ex);
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ConfigureIdentityTables();
        builder.ApplyConfigurationsFromAssembly(typeof(MonitorDbContext).Assembly);
    }
}
