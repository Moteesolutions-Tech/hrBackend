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

internal sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("branches");

        builder.HasKey(branch => branch.Id);

        builder.Property(branch => branch.Name).HasMaxLength(150).IsRequired();
        builder.Property(branch => branch.Code).HasMaxLength(20).IsRequired();
        builder.Property(branch => branch.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(branch => branch.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(branch => branch.City).HasMaxLength(100);
        builder.Property(branch => branch.Region).HasMaxLength(100);
        builder.Property(branch => branch.PostalCode).HasMaxLength(20);
        builder.Property(branch => branch.Country).HasMaxLength(100);
        builder.Property(branch => branch.TimeZone).HasMaxLength(64);
        builder.Property(branch => branch.Phone).HasMaxLength(40);
        builder.Property(branch => branch.Email).HasMaxLength(256);

        // jsonb, like the other list-valued columns here. Address lines are read and
        // written whole and never queried by their parts.
        builder.Property(branch => branch.AddressLines)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Json),
                json => JsonSerializer.Deserialize<IReadOnlyList<string>>(json, Json)!,
                new ValueComparer<IReadOnlyList<string>>(
                    (left, right) => JsonSerializer.Serialize(left, Json)
                        == JsonSerializer.Serialize(right, Json),
                    value => JsonSerializer.Serialize(value, Json).GetHashCode(
                        StringComparison.Ordinal),
                    value => JsonSerializer.Deserialize<IReadOnlyList<string>>(
                        JsonSerializer.Serialize(value, Json), Json)!));

        // RESTRICT, matching a department's head: deleting somebody who still runs a site
        // is the deletion that should be stopped, not one that silently leaves the site
        // without a manager.
        builder.HasOne<Domain.Employees.Employee>()
            .WithMany()
            .HasForeignKey(branch => branch.ManagerEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        // The code appears on a badge and in a picker, where two identical ones cannot be
        // chosen between.
        builder.HasIndex(branch => new { branch.TenantId, branch.Code })
            .HasDatabaseName("ix_branches_tenant_code")
            .IsUnique();
    }
}
