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
