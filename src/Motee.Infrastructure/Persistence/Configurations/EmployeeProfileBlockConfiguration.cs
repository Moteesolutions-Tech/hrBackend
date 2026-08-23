using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Employees;
using Motee.Domain.Tenants;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class EmployeeBankDetailsConfiguration : IEntityTypeConfiguration<EmployeeBankDetails>
{
    public void Configure(EntityTypeBuilder<EmployeeBankDetails> builder)
    {
        builder.ToTable("employee_bank_details");

        builder.HasKey(details => details.Id);

        builder.Property(details => details.BankName).HasMaxLength(150);
        builder.Property(details => details.AccountNumber).HasMaxLength(34);
        builder.Property(details => details.SortCode).HasMaxLength(20);
        builder.Property(details => details.AccountHolderName).HasMaxLength(150);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(details => details.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deleting the employee takes their bank details with them; there is nothing
        // left to attach them to.
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(details => details.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per employee, enforced rather than assumed.
        builder.HasIndex(details => details.EmployeeId)
            .HasDatabaseName("ix_employee_bank_details_employee")
            .IsUnique();
    }
}

internal sealed class EmployeeIdentityDocumentsConfiguration
    : IEntityTypeConfiguration<EmployeeIdentityDocuments>
{
    public void Configure(EntityTypeBuilder<EmployeeIdentityDocuments> builder)
    {
        builder.ToTable("employee_identity_documents");

        builder.HasKey(documents => documents.Id);

        builder.Property(documents => documents.NationalIdNumber).HasMaxLength(50);
        builder.Property(documents => documents.TaxIdNumber).HasMaxLength(50);
        builder.Property(documents => documents.PensionId).HasMaxLength(50);
        builder.Property(documents => documents.HousingFundNumber).HasMaxLength(50);
        builder.Property(documents => documents.DrivingLicenceNumber).HasMaxLength(50);
        builder.Property(documents => documents.PassportNumber).HasMaxLength(50);
        builder.Property(documents => documents.PassportIssuingCountry).HasMaxLength(100);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(documents => documents.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(documents => documents.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(documents => documents.EmployeeId)
            .HasDatabaseName("ix_employee_identity_documents_employee")
            .IsUnique();
    }
}

internal sealed class EmployeeMedicalConfiguration : IEntityTypeConfiguration<EmployeeMedical>
{
    public void Configure(EntityTypeBuilder<EmployeeMedical> builder)
    {
        builder.ToTable("employee_medical");

        builder.HasKey(medical => medical.Id);

        builder.Property(medical => medical.Allergies).HasMaxLength(1000);
        builder.Property(medical => medical.Conditions).HasMaxLength(1000);
        builder.Property(medical => medical.Medications).HasMaxLength(1000);
        builder.Property(medical => medical.DietaryRequirements).HasMaxLength(1000);
        builder.Property(medical => medical.AccessibilityNeeds).HasMaxLength(1000);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(medical => medical.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(medical => medical.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(medical => medical.EmployeeId)
            .HasDatabaseName("ix_employee_medical_employee")
            .IsUnique();
    }
}
