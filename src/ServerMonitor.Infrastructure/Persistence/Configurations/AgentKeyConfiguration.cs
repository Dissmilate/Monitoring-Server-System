using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ServerMonitor.Application.Security;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Infrastructure.Persistence.Configurations;

internal sealed class AgentKeyConfiguration : IEntityTypeConfiguration<AgentKey>
{
    private const int Sha256HexLength = 64;

    public void Configure(EntityTypeBuilder<AgentKey> builder)
    {
        builder.ToTable("agent_keys");
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).ValueGeneratedNever();

        builder.Property(k => k.Prefix).HasMaxLength(ApiKeys.PrefixLength).IsFixedLength().IsRequired();
        builder.HasIndex(k => k.Prefix).IsUnique();

        builder.Property(k => k.KeyHash).HasMaxLength(Sha256HexLength).IsFixedLength().IsRequired();

        builder.Ignore(k => k.IsActive);
    }
}
