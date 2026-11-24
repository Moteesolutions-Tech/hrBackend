using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Health;
using Motee.Domain.Sickness;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class OhReferralConfiguration : IEntityTypeConfiguration<OhReferral>
{
    public void Configure(EntityTypeBuilder<OhReferral> builder)
    {
        builder.ToTable("oh_referrals");

        builder.HasKey(referral => referral.Id);

        builder.Property(referral => referral.ReferenceNumber).HasMaxLength(40).IsRequired();

        builder.Property(referral => referral.Stage)
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(referral => referral.Fitness)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(referral => referral.CaseNotes)
            .HasMaxLength(OhRules.MaximumNotesLength);

        builder.Property(referral => referral.ReasonableAdjustmentsConsidered)
            .HasColumnName("adjustments_considered");

        // Set null rather than cascade: deleting an absence record must not
        // delete the record of the referral it caused.
        builder.HasOne<SicknessRecord>()
            .WithMany()
            .HasForeignKey(referral => referral.SicknessRecordId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(referral => new { referral.TenantId, referral.ReferenceNumber })
            .IsUnique();

        builder.HasIndex(referral =>
            new { referral.TenantId, referral.Stage, referral.AbsenceStartDate });

        builder.HasIndex(referral => new { referral.TenantId, referral.ReviewDate });
    }
}

internal sealed class OhAdjustmentConfiguration : IEntityTypeConfiguration<OhAdjustment>
{
    public void Configure(EntityTypeBuilder<OhAdjustment> builder)
    {
        builder.ToTable("oh_adjustments");

        builder.HasKey(adjustment => adjustment.Id);

        builder.Property(adjustment => adjustment.Description).HasMaxLength(1000).IsRequired();

        builder.Property(adjustment => adjustment.Stage)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(adjustment => adjustment.ImplementationOwnerEmployeeId)
            .HasColumnName("owner_employee_id");

        builder.HasOne<OhReferral>()
            .WithMany()
            .HasForeignKey(adjustment => adjustment.ReferralId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<SicknessRecord>()
            .WithMany()
            .HasForeignKey(adjustment => adjustment.SicknessRecordId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(adjustment => new { adjustment.ReferralId, adjustment.Stage });

        builder.HasIndex(adjustment => new { adjustment.TenantId, adjustment.ReviewDate });
    }
}
