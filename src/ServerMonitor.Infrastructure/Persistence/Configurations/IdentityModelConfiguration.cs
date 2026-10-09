using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ServerMonitor.Infrastructure.Identity;

namespace ServerMonitor.Infrastructure.Persistence.Configurations;

internal static class IdentityModelConfiguration
{
    public static void ConfigureIdentityTables(this ModelBuilder builder)
    {
        builder.Entity<AppUser>(user =>
        {
            user.ToTable("users");
            user.HasIndex(u => u.NormalizedUserName).HasDatabaseName("ix_users_normalized_user_name");
            user.HasIndex(u => u.NormalizedEmail).HasDatabaseName("ix_users_normalized_email");
        });

        builder.Entity<IdentityRole<Guid>>(role =>
        {
            role.ToTable("roles");
            role.HasIndex(r => r.NormalizedName).HasDatabaseName("ix_roles_normalized_name");
        });

        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");
    }
}
