using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ServerMonitor.Domain.Agents;
using ServerMonitor.Domain.Servers;

namespace ServerMonitor.Infrastructure.Persistence.Configurations;

internal sealed class AgentCommandConfiguration : IEntityTypeConfiguration<AgentCommand>
{
    public void Configure(EntityTypeBuilder<AgentCommand> builder)
    {
        builder.ToTable("agent_commands");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.Type).HasConversion<string>().HasMaxLength(32);
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.ResultMessage).HasMaxLength(AgentCommand.MaxResultMessageLength);

        builder.HasOne<Server>()
            .WithMany()
            .HasForeignKey(c => c.ServerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => new { c.ServerId, c.CreatedAt });

        builder.HasIndex(c => c.CreatedAt)
            .HasDatabaseName("ix_agent_commands_unpublished")
            .HasFilter("published_at IS NULL");

        builder.Ignore(c => c.IsCompleted);
    }
}
