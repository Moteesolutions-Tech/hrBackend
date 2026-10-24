using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Attendance;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class AttendanceRecordConfiguration : IEntityTypeConfiguration<AttendanceRecord>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<AttendanceRecord> builder)
    {
        builder.ToTable("attendance_records");

        builder.HasKey(record => record.Id);

        // Enums as their names, like every other enum here, so a day is readable in
        // psql and a reordered enum cannot silently turn Remote into Office.
        builder.Property(record => record.Location)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(record => record.Source)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(record => record.LocationName).HasMaxLength(200);
        builder.Property(record => record.ClockInCoordinates).HasMaxLength(50);
        builder.Property(record => record.ClockOutCoordinates).HasMaxLength(50);
        builder.Property(record => record.Note).HasMaxLength(1000);

        // jsonb. Breaks are read and written with the day and never queried across —
        // "who took the longest lunch" is not a question anybody has asked, and a
        // child table to answer it costs a join on every read of every row.
        builder.Property(record => record.Breaks)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Json),
                json => JsonSerializer.Deserialize<List<BreakInterval>>(json, Json)!,
                new ValueComparer<List<BreakInterval>>(
                    (left, right) => JsonSerializer.Serialize(left, Json)
                        == JsonSerializer.Serialize(right, Json),
                    value => JsonSerializer.Serialize(value, Json).GetHashCode(),
                    value => JsonSerializer.Deserialize<List<BreakInterval>>(
                        JsonSerializer.Serialize(value, Json), Json)!))
            .IsRequired();

        // One person, one day. The clock reads this row before every punch, so a
        // duplicate would not be a tidiness problem — it would be two half-days, one
        // of which is invisible, and whichever came back first would win.
        builder.HasIndex(record => new { record.TenantId, record.EmployeeId, record.Date })
            .IsUnique();

        // The overview screen's query: one company, one day.
        builder.HasIndex(record => new { record.TenantId, record.Date });
    }
}
