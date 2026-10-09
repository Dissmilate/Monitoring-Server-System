using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ServerMonitor.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MonitorDbContext>
{
    public MonitorDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MonitorDbContext>();
        DependencyInjection.ConfigureDbContext(options, "Host=localhost;Database=server_monitor");
        return new MonitorDbContext(options.Options);
    }
}
