using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Audit;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_entries");

        builder.HasKey(entry => entry.Id);

        // Stored as text, like every other enum here, so a row is readable in psql and
        // reordering the enum cannot silently relabel three years of history.
        builder.Property(entry => entry.Action)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(entry => entry.Module).HasMaxLength(60).IsRequired();
        builder.Property(entry => entry.EntityType).HasMaxLength(60);
        builder.Property(entry => entry.Description).HasMaxLength(500).IsRequired();
        builder.Property(entry => entry.ActorName).HasMaxLength(320);
        builder.Property(entry => entry.Endpoint).HasMaxLength(300);
        builder.Property(entry => entry.HttpMethod).HasMaxLength(10);
        builder.Property(entry => entry.IpAddress).HasMaxLength(64);
        builder.Property(entry => entry.UserAgent).HasMaxLength(400);
        builder.Property(entry => entry.CorrelationId).HasMaxLength(100);

        builder.Property(entry => entry.Changes).HasColumnType("jsonb");

        // No foreign key to users on purpose. The actor is denormalised, and a
        // constraint would either block deleting a user or cascade away their history —
        // both of which defeat keeping a record of what they did.
        builder.Property(entry => entry.ActorUserId);

        // The screen's default view: this tenant, newest first.
        builder.HasIndex(entry => new { entry.TenantId, entry.CreatedAt })
            .HasDatabaseName("ix_audit_entries_tenant_created");

        // "Show me everything that happened to this employee" — the question an auditor
        // actually asks, and a scan without this.
        builder.HasIndex(entry => new { entry.TenantId, entry.EntityId })
            .HasDatabaseName("ix_audit_entries_entity");

        builder.HasIndex(entry => new { entry.TenantId, entry.Module })
            .HasDatabaseName("ix_audit_entries_module");

        builder.HasIndex(entry => new { entry.TenantId, entry.ActorUserId })
            .HasDatabaseName("ix_audit_entries_actor");
    }
}
