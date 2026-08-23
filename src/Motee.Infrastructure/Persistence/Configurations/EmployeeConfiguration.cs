using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.ToTable("employees");

        builder.HasKey(employee => employee.Id);

        builder.Property(employee => employee.EmployeeNumber).HasMaxLength(50);
        builder.Property(employee => employee.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(employee => employee.MiddleName).HasMaxLength(100);
        builder.Property(employee => employee.LastName).HasMaxLength(100).IsRequired();
        builder.Property(employee => employee.Email).HasMaxLength(320).IsRequired();
        builder.Property(employee => employee.Phone).HasMaxLength(50);
        builder.Property(employee => employee.JobTitle).HasMaxLength(150);
        builder.Property(employee => employee.Title).HasMaxLength(20);
        builder.Property(employee => employee.PreferredName).HasMaxLength(100);
        builder.Property(employee => employee.MaidenName).HasMaxLength(100);
        builder.Property(employee => employee.Initials).HasMaxLength(10);
        builder.Property(employee => employee.Gender).HasMaxLength(50);
        builder.Property(employee => employee.Nationality).HasMaxLength(100);
        builder.Property(employee => employee.MaritalStatus).HasMaxLength(50);
        builder.Property(employee => employee.Ethnicity).HasMaxLength(100);
        builder.Property(employee => employee.Address).HasMaxLength(500);
        builder.Property(employee => employee.State).HasMaxLength(100);
        builder.Property(employee => employee.WorkLocation).HasMaxLength(150);
        builder.Property(employee => employee.WorkMode).HasConversion<string>().HasMaxLength(20);
        builder.Property(employee => employee.Grade).HasMaxLength(50);
        builder.Property(employee => employee.EmergencyContactName).HasMaxLength(150);
        builder.Property(employee => employee.EmergencyContactRelationship).HasMaxLength(100);
        builder.Property(employee => employee.EmergencyContactPhone).HasMaxLength(50);

        builder.Property(employee => employee.CountryOfEmployment).HasMaxLength(100);

        builder.Property(employee => employee.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(employee => employee.OnboardingMethod).HasConversion<string>().HasMaxLength(20);

        // A fixed taxonomy, so it is a column on the employee rather than a foreign
        // key to a table every tenant would need seeded.
        builder.Property(employee => employee.EmploymentType).HasConversion<string>().HasMaxLength(30);

        // FullName is composed in the domain.
        builder.Ignore(employee => employee.FullName);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(employee => employee.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(employee => employee.DepartmentId)
            .OnDelete(DeleteBehavior.SetNull);

        // The org hierarchy. Restrict rather than cascade: deleting a manager must
        // not delete their reports.
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(employee => employee.ManagerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Scoped to the tenant, not global — two companies may both employ the same
        // person, and both may use employee number 001.
        builder.HasIndex(employee => new { employee.TenantId, employee.Email })
            .HasDatabaseName("ix_employees_tenant_email")
            .IsUnique();

        builder.HasIndex(employee => new { employee.TenantId, employee.EmployeeNumber })
            .HasDatabaseName("ix_employees_tenant_number")
            .IsUnique()
            .HasFilter("employee_number IS NOT NULL");

        // PermissionScope.Team narrows on this.
        builder.HasIndex(employee => employee.ManagerId).HasDatabaseName("ix_employees_manager");
        builder.HasIndex(employee => employee.TenantId).HasDatabaseName("ix_employees_tenant");
    }
}
