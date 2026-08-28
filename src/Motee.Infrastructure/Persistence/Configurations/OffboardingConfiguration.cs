using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Offboarding;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class OffboardingRecordConfiguration : IEntityTypeConfiguration<OffboardingRecord>
{
    public void Configure(EntityTypeBuilder<OffboardingRecord> builder)
    {
        builder.ToTable("offboarding_records");

        builder.HasKey(record => record.Id);

        // Text, like every other enum here: readable in psql, and reordering the enum
        // cannot silently relabel why somebody left.
        builder.Property(record => record.Status)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(record => record.ExitReason)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(record => record.Notes).HasMaxLength(2000);
        builder.Property(record => record.DecisionReason).HasMaxLength(1000);
        builder.Property(record => record.ExitInterviewNotes).HasMaxLength(4000);

        // RESTRICT, not CASCADE. Deleting an employee must not erase the record of them
        // leaving — that record is often the only thing that explains the gap.
        builder.HasOne<Domain.Employees.Employee>()
            .WithMany()
            .HasForeignKey(record => record.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(record => new { record.TenantId, record.Status })
            .HasDatabaseName("ix_offboarding_tenant_status");

        // One live exit per person. A filtered unique index rather than a check in code
        // alone, because two clicks a second apart both pass the check and the second
        // insert is what has to fail.
        builder.HasIndex(record => record.EmployeeId)
            .HasDatabaseName("ix_offboarding_employee_open")
            .IsUnique()
            .HasFilter("status IN ('Pending', 'Approved', 'InProgress')");
    }
}

internal sealed class OffboardingClearanceItemConfiguration
    : IEntityTypeConfiguration<OffboardingClearanceItem>
{
    public void Configure(EntityTypeBuilder<OffboardingClearanceItem> builder)
    {
        builder.ToTable("offboarding_clearance_items");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Label).HasMaxLength(200).IsRequired();
        builder.Property(item => item.Department).HasMaxLength(100).IsRequired();
        builder.Property(item => item.Notes).HasMaxLength(1000);

        // CASCADE here, unlike the employee link: a clearance item has no meaning apart
        // from the exit it belongs to.
        builder.HasOne<OffboardingRecord>()
            .WithMany()
            .HasForeignKey(item => item.OffboardingRecordId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(item => item.OffboardingRecordId)
            .HasDatabaseName("ix_offboarding_clearance_record");
    }
}
