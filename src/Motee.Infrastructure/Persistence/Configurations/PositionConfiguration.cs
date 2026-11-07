using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Organisation;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class PositionConfiguration : IEntityTypeConfiguration<Position>
{
    public void Configure(EntityTypeBuilder<Position> builder)
    {
        builder.ToTable("positions");

        builder.HasKey(position => position.Id);

        builder.Property(position => position.Title).HasMaxLength(200).IsRequired();
        builder.Property(position => position.Grade).HasMaxLength(60);
        builder.Property(position => position.Description).HasMaxLength(2000);

        // One title per department. The same title in two departments is
        // ordinary; two in one department is two answers to "which is Sarah
        // in". Department-scoped rather than tenant-scoped for that reason.
        builder
            .HasIndex(position => new { position.TenantId, position.DepartmentId, position.Title })
            .IsUnique()
            .HasDatabaseName("ix_positions_one_title_per_department");

        builder.HasIndex(position => new { position.TenantId, position.IsActive });
    }
}
