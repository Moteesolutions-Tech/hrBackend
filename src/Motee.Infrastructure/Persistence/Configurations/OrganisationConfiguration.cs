using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.ToTable("departments");
        builder.HasKey(department => department.Id);

        builder.Property(department => department.Name).HasMaxLength(150).IsRequired();

        // The create modal caps input at 6 and uppercases it; the column leaves room
        // for tenants importing longer existing codes.
        builder.Property(department => department.Code).HasMaxLength(20).IsRequired();

        builder.Property(department => department.Description).HasMaxLength(1000);

        builder.Property(department => department.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // Money. Precision is fixed rather than floating so budgets total exactly.
        builder.Property(department => department.BudgetMonthly).HasPrecision(18, 2);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(department => department.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(department => new { department.TenantId, department.Name })
            .HasDatabaseName("ix_departments_tenant_name")
            .IsUnique();

        // Both identify a department to the tenant, so both must be unique to it.
        builder.HasIndex(department => new { department.TenantId, department.Code })
            .HasDatabaseName("ix_departments_tenant_code")
            .IsUnique();
    }
}
