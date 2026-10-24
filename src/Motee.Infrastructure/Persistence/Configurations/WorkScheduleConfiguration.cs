using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Attendance;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class WorkScheduleConfiguration : IEntityTypeConfiguration<WorkSchedule>
{
    // Day names rather than their numbers, for the same reason every other enum here
    // is stored as text: "monday" survives somebody reordering DayOfWeek, and a shift
    // pattern is something people read in psql while working out what went wrong.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        },
    };

    public void Configure(EntityTypeBuilder<WorkSchedule> builder)
    {
        builder.ToTable("work_schedules");

        builder.HasKey(schedule => schedule.Id);

        builder.Property(schedule => schedule.Name).HasMaxLength(100).IsRequired();

        // jsonb rather than a child table. The days are read together, written
        // together and never queried across — "which schedules work Saturdays" is not
        // a question anybody has asked, and a seven-row join per schedule to answer it
        // in advance is a cost paid on every read.
        builder.Property(schedule => schedule.Days)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Json),
                json => JsonSerializer.Deserialize<Dictionary<DayOfWeek, WorkingHours>>(json, Json)!,
                new ValueComparer<Dictionary<DayOfWeek, WorkingHours>>(
                    (left, right) => JsonSerializer.Serialize(left, Json)
                        == JsonSerializer.Serialize(right, Json),
                    value => JsonSerializer.Serialize(value, Json).GetHashCode(),
                    value => JsonSerializer.Deserialize<Dictionary<DayOfWeek, WorkingHours>>(
                        JsonSerializer.Serialize(value, Json), Json)!))
            .IsRequired();

        builder.HasIndex(schedule => new { schedule.TenantId, schedule.Name })
            .IsUnique();

        // One default per company, enforced here rather than in a service. Two of them
        // is not a validation failure somebody can see — it is a silent coin toss over
        // which hours a new hire is judged against.
        builder.HasIndex(schedule => schedule.TenantId)
            .IsUnique()
            .HasFilter("is_default")
            .HasDatabaseName("ix_work_schedules_one_default_per_tenant");
    }
}
