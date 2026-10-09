using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Infrastructure.Persistence.Configurations;

internal sealed class EnrollmentTokenConfiguration : IEntityTypeConfiguration<EnrollmentToken>
{
    private const int Sha256HexLength = 64;

    public void Configure(EntityTypeBuilder<EnrollmentToken> builder)
    {
        builder.ToTable("enrollment_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.TokenHash).HasMaxLength(Sha256HexLength).IsFixedLength().IsRequired();
        builder.HasIndex(t => t.TokenHash).IsUnique();
    }
}
