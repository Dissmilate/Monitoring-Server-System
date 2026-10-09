using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Infrastructure.Persistence.Configurations;

internal sealed class ServerConfiguration : IEntityTypeConfiguration<Server>
{
    public void Configure(EntityTypeBuilder<Server> builder)
    {
        builder.ToTable("servers");
        builder.HasKey(s => s.Id);

        // Ids come from the domain (UUIDv7).
        builder.Property(s => s.Id).ValueGeneratedNever();

        // Not xmin: presence updates rewrite the row on every heartbeat.
        builder.Property(s => s.Version).IsConcurrencyToken();

        builder.Property(s => s.MetricsIntervalSeconds).HasDefaultValue(Server.DefaultMetricsIntervalSeconds);
        builder.Property(s => s.ConfigVersion).HasDefaultValue(1L);

        builder.Property(s => s.Name).HasMaxLength(Server.MaxNameLength).IsRequired();
        builder.HasIndex(s => s.Name).IsUnique();

        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.Hostname).HasMaxLength(AgentHost.MaxHostnameLength);
        builder.Property(s => s.OperatingSystem).HasMaxLength(AgentHost.MaxOperatingSystemLength);
        builder.Property(s => s.AgentVersion).HasMaxLength(AgentHost.MaxAgentVersionLength);

        builder.HasMany(s => s.EnrollmentTokens)
            .WithOne()
            .HasForeignKey(t => t.ServerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(s => s.EnrollmentTokens)
            .HasField("_enrollmentTokens")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(s => s.AgentKeys)
            .WithOne()
            .HasForeignKey(k => k.ServerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(s => s.AgentKeys)
            .HasField("_agentKeys")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(s => s.ActiveAgentKey);
    }
}
