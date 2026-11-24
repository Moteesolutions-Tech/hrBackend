using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Attendance;
using Motee.Domain.Employees;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class EmployeeNoteConfiguration : IEntityTypeConfiguration<EmployeeNote>
{
    public void Configure(EntityTypeBuilder<EmployeeNote> builder)
    {
        builder.ToTable("employee_notes");

        builder.HasKey(note => note.Id);

        builder.Property(note => note.Body)
            .HasMaxLength(EmployeeNotes.MaximumBodyLength)
            .IsRequired();

        builder.Property(note => note.AuthorName).HasMaxLength(200);

        builder.Property(note => note.Kind)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(note => note.Audience)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(note => note.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(note => new { note.EmployeeId, note.Pinned, note.CreatedAt });

        builder.HasIndex(note => new { note.TenantId, note.RemindAt });
    }
}

internal sealed class ShiftAssignmentConfiguration : IEntityTypeConfiguration<ShiftAssignment>
{
    public void Configure(EntityTypeBuilder<ShiftAssignment> builder)
    {
        builder.ToTable("shift_assignments");

        builder.HasKey(assignment => assignment.Id);

        builder.Property(assignment => assignment.Note).HasMaxLength(500);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(assignment => assignment.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restricted rather than cascaded: deleting a work schedule must not
        // silently empty a published rota.
        builder.HasOne<WorkSchedule>()
            .WithMany()
            .HasForeignKey(assignment => assignment.WorkScheduleId)
            .OnDelete(DeleteBehavior.Restrict);

        // One shift per person per day. Two would mean the grid has two cells
        // for one square and no rule for which to draw.
        builder.HasIndex(assignment => new { assignment.EmployeeId, assignment.Date })
            .IsUnique();

        builder.HasIndex(assignment => new { assignment.TenantId, assignment.Date });
    }
}
