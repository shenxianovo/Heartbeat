using Heartbeat.Management;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Heartbeat.Persistence.Configurations;

internal sealed class HubNodeConfiguration : IEntityTypeConfiguration<HubNode>
{
    public void Configure(EntityTypeBuilder<HubNode> builder)
    {
        builder.ToTable("hubs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.OwnerId).HasColumnName("owner_id");
        builder.Property(x => x.SessionId).HasColumnName("session_id");
        builder.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
        builder.Property(x => x.RetiredAt).HasColumnName("retired_at");
        builder.Property(x => x.StatusJson).HasColumnName("status").HasColumnType("jsonb");
        builder.HasIndex(x => x.OwnerId);
    }
}
