using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Attendance;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class AttendanceCorrectionConfiguration
    : IEntityTypeConfiguration<AttendanceCorrection>
{
    public void Configure(EntityTypeBuilder<AttendanceCorrection> builder)
    {
        builder.ToTable("attendance_corrections");

        builder.HasKey(correction => correction.Id);

        builder.Property(correction => correction.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(correction => correction.Reason).HasMaxLength(1000).IsRequired();
        builder.Property(correction => correction.ResolutionNote).HasMaxLength(1000);

        // One open claim per day. Two would be two answers to the same question, and
        // approving both applies whichever was read last.
        //
        // Partial, so the history survives: a day can be corrected, corrected again
        // months later, and keep both records — it is only the open ones that must
        // be unique.
        builder.HasIndex(correction =>
                new { correction.TenantId, correction.EmployeeId, correction.Date })
            .IsUnique()
            .HasFilter("status = 'Pending'")
            .HasDatabaseName("ix_attendance_corrections_one_pending_per_day");

        // The review queue's query.
        builder.HasIndex(correction => new { correction.TenantId, correction.Status });
    }
}
