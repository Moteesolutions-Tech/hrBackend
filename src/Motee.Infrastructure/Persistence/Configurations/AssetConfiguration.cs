using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Assets;
using Motee.Domain.Employees;
using Motee.Domain.Tenants;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.ToTable("assets");

        builder.HasKey(asset => asset.Id);

        builder.Property(asset => asset.Tag).HasMaxLength(50).IsRequired();
        builder.Property(asset => asset.Name).HasMaxLength(150).IsRequired();
        builder.Property(asset => asset.Category).HasMaxLength(100);
        builder.Property(asset => asset.SerialNumber).HasMaxLength(100);
        builder.Property(asset => asset.Notes).HasMaxLength(1000);
        builder.Property(asset => asset.Status).HasConversion<string>().HasMaxLength(20);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(asset => asset.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, not SetNull: nulling the holder would leave the asset reading
        // Assigned with nobody holding it, which is a lie the ck_assets_assignment
        // constraint rejects anyway. Someone still holding a laptop cannot be erased
        // until it is returned. The app soft-deletes employees, so this only bites on
        // a hard delete — where being stopped is the point.
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(asset => asset.AssignedToEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        // Scoped to the tenant: two companies may both label something "AST-0001".
        builder.HasIndex(asset => new { asset.TenantId, asset.Tag })
            .HasDatabaseName("ix_assets_tenant_tag")
            .IsUnique();

        // Behind "what is this person holding", on the employee detail page and at
        // offboarding.
        builder.HasIndex(asset => asset.AssignedToEmployeeId)
            .HasDatabaseName("ix_assets_assigned_to");

        builder.HasIndex(asset => new { asset.TenantId, asset.Status })
            .HasDatabaseName("ix_assets_tenant_status");
    }
}

internal sealed class AssetAssignmentConfiguration : IEntityTypeConfiguration<AssetAssignment>
{
    public void Configure(EntityTypeBuilder<AssetAssignment> builder)
    {
        builder.ToTable("asset_assignments");

        builder.HasKey(assignment => assignment.Id);

        builder.Property(assignment => assignment.ReturnReason).HasMaxLength(200);
        builder.Property(assignment => assignment.ConditionOnAssign).HasMaxLength(500);
        builder.Property(assignment => assignment.ConditionOnReturn).HasMaxLength(500);

        builder.Ignore(assignment => assignment.IsOpen);

        // CASCADE from the asset: history of a thing that no longer exists is not history
        // anybody can act on, and an asset is hard-deletable.
        builder.HasOne<Asset>()
            .WithMany()
            .HasForeignKey(assignment => assignment.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        // RESTRICT to the employee, matching the asset's own pointer: deleting somebody who
        // still appears in an asset's history is the deletion that should be stopped.
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(assignment => assignment.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(assignment => assignment.AssetId)
            .HasDatabaseName("ix_asset_assignments_asset");

        // "What has this person ever held", for an offboarding clearance check.
        builder.HasIndex(assignment => assignment.EmployeeId)
            .HasDatabaseName("ix_asset_assignments_employee");

        // At most one open spell per asset. Without this, a reassignment that failed to
        // close the previous row leaves two people holding the same laptop according to
        // the history, and no query can tell which is right.
        builder.HasIndex(assignment => assignment.AssetId)
            .IsUnique()
            .HasFilter("returned_on IS NULL")
            .HasDatabaseName("ix_asset_assignments_open");
    }
}
