using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServerMonitor.Contracts.Auth;
using ServerMonitor.Infrastructure.Identity;

namespace ServerMonitor.Infrastructure.Persistence;

public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public string? AdminEmail { get; set; }

    public string? AdminPassword { get; set; }
}

public sealed partial class DatabaseInitializer(
    MonitorDbContext db,
    RoleManager<IdentityRole<Guid>> roleManager,
    UserManager<AppUser> userManager,
    IOptions<SeedOptions> seed,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await db.Database.MigrateAsync(ct);
        LogMigrated(logger);

        await SeedRolesAsync();
        await SeedAdminAsync();
    }

    private async Task SeedRolesAsync()
    {
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                EnsureSucceeded(await roleManager.CreateAsync(new IdentityRole<Guid>(role)), $"create role {role}");
            }
        }
    }

    private async Task SeedAdminAsync()
    {
        var (email, password) = (seed.Value.AdminEmail, seed.Value.AdminPassword);
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            LogAdminSeedSkipped(logger);
            return;
        }

        if (await userManager.FindByEmailAsync(email) is not null)
        {
            return;
        }

        var admin = new AppUser { UserName = email, Email = email, EmailConfirmed = true };
        EnsureSucceeded(await userManager.CreateAsync(admin, password), "create admin user");
        EnsureSucceeded(await userManager.AddToRoleAsync(admin, Roles.Admin), "assign admin role");
        LogAdminCreated(logger, email);
    }

    private static void EnsureSucceeded(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Failed to {action}: {errors}");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrations applied")]
    private static partial void LogMigrated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Seed:AdminEmail or Seed:AdminPassword is not set; initial admin was not created")]
    private static partial void LogAdminSeedSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Initial admin {Email} created")]
    private static partial void LogAdminCreated(ILogger logger, string email);
}
