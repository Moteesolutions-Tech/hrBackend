using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Attendance;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class TimesheetConfiguration : IEntityTypeConfiguration<Timesheet>
{
    public void Configure(EntityTypeBuilder<Timesheet> builder)
    {
        builder.ToTable("timesheets");

        builder.HasKey(sheet => sheet.Id);

        builder.Property(sheet => sheet.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(sheet => sheet.RejectionReason).HasMaxLength(1000);

        // Money-adjacent, so stored exactly rather than as a float. Four decimal
        // places is more than hours need and costs nothing; two would round a
        // quarter-hour fine and a third of one badly.
        builder.Property(sheet => sheet.ApprovedTotalHours).HasPrecision(10, 4);
        builder.Property(sheet => sheet.ApprovedOvertimeHours).HasPrecision(10, 4);

        // One person, one week. Two rows would be two lifecycles over the same days,
        // and whichever was read first would decide whether the week was approved.
        builder.HasIndex(sheet => new { sheet.TenantId, sheet.EmployeeId, sheet.WeekStart })
            .IsUnique();

        // The review queue's query.
        builder.HasIndex(sheet => new { sheet.TenantId, sheet.Status });
    }
}
