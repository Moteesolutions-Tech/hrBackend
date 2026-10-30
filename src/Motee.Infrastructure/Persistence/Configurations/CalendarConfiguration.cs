using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Calendar;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class CalendarEventConfiguration : IEntityTypeConfiguration<CalendarEvent>
{
    public void Configure(EntityTypeBuilder<CalendarEvent> builder)
    {
        builder.ToTable("calendar_events");

        builder.HasKey(calendarEvent => calendarEvent.Id);

        builder.Property(calendarEvent => calendarEvent.Title).HasMaxLength(200).IsRequired();

        builder.Property(calendarEvent => calendarEvent.Category)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(calendarEvent => calendarEvent.Audience)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // Every view asks the same question — what falls inside this window —
        // and asks it of one company at a time.
        builder.HasIndex(calendarEvent => new { calendarEvent.TenantId, calendarEvent.Date });
    }
}

internal sealed class CalendarEventAttendeeConfiguration
    : IEntityTypeConfiguration<CalendarEventAttendee>
{
    public void Configure(EntityTypeBuilder<CalendarEventAttendee> builder)
    {
        builder.ToTable("calendar_event_attendees");

        builder.HasKey(attendee => attendee.Id);

        // Declared so EF inserts the event before the rows pointing at it.
        // Without the relationship the ordering is arbitrary, and a create that
        // names its guests in the same unit of work fails on the foreign key.
        builder.HasOne<CalendarEvent>()
            .WithMany()
            .HasForeignKey(attendee => attendee.CalendarEventId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(attendee => attendee.CalendarEventId);

        builder.HasIndex(attendee => attendee.EmployeeId);

        // Saving twice should not put somebody on the same event twice, which
        // would draw the event on their day twice.
        builder
            .HasIndex(attendee => new { attendee.CalendarEventId, attendee.EmployeeId })
            .IsUnique()
            .HasDatabaseName("ix_calendar_event_attendees_one_per_person");
    }
}
