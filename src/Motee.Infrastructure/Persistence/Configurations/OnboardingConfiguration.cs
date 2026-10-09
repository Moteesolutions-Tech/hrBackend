using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Onboarding;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class OnboardingRecordConfiguration : IEntityTypeConfiguration<OnboardingRecord>
{
    public void Configure(EntityTypeBuilder<OnboardingRecord> builder)
    {
        builder.ToTable("onboarding_records");

        builder.HasKey(record => record.Id);

        // Text, like every other enum here: readable in psql, and reordering the enum
        // cannot silently move everyone to a different stage.
        builder.Property(record => record.Stage)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(record => record.Submission)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        // CASCADE, unlike the offboarding record beside it, and the difference matters.
        //
        // An offboarding record is the history of somebody leaving — often the only thing
        // that explains a gap — so it survives them. An onboarding record exists for
        // every employee from the moment they are created, and describes a process rather
        // than an outcome. RESTRICT here would mean no employee could ever be deleted,
        // which is not this table's decision to make.
        builder.HasOne<Domain.Employees.Employee>()
            .WithMany()
            .HasForeignKey(record => record.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

        // One onboarding per person, enforced by the database rather than by EnsureAsync
        // alone: two requests a moment apart both find nothing and both insert, and it is
        // the second insert that has to fail.
        builder.HasIndex(record => record.EmployeeId)
            .HasDatabaseName("ix_onboarding_employee")
            .IsUnique();

        // The pipeline screen's default view: this tenant, filtered by stage.
        builder.HasIndex(record => new { record.TenantId, record.Stage })
            .HasDatabaseName("ix_onboarding_tenant_stage");

        // No foreign key to the approval instance on purpose. The link is one-way — a
        // module knows its approval, the engine knows nothing of its modules — and a
        // constraint here would be the engine's schema depending on onboarding's.
        builder.HasIndex(record => record.ApprovalInstanceId)
            .HasDatabaseName("ix_onboarding_approval");
    }
}
