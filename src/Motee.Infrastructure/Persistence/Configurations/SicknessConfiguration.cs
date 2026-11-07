using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Sickness;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class SicknessRecordConfiguration : IEntityTypeConfiguration<SicknessRecord>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<SicknessRecord> builder)
    {
        builder.ToTable("sickness_records");

        builder.HasKey(record => record.Id);

        foreach (string property in (string[])
        [
            nameof(SicknessRecord.Kind),
            nameof(SicknessRecord.StartPortion),
            nameof(SicknessRecord.EndPortion),
            nameof(SicknessRecord.Reason),
            nameof(SicknessRecord.Source),
            nameof(SicknessRecord.ApprovalStatus),
            nameof(SicknessRecord.AbsenceState),
            nameof(SicknessRecord.RtwStatus),
        ])
        {
            builder.Property(property).HasConversion<string>().HasMaxLength(30).IsRequired();
        }

        builder.Property(record => record.ExclusionCategory)
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(record => record.ReportedByName).HasMaxLength(200);

        // jsonb, like every other small list here: written with the row, read
        // with the row, never queried across. Fit-note status is worked out in
        // memory from these, which is the only thing that reads them.
        builder.Property(record => record.Documents)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Json),
                json => JsonSerializer.Deserialize<List<SicknessDocument>>(json, Json)!,
                new ValueComparer<List<SicknessDocument>>(
                    (left, right) => JsonSerializer.Serialize(left, Json)
                        == JsonSerializer.Serialize(right, Json),
                    value => JsonSerializer.Serialize(value, Json).GetHashCode(),
                    value => JsonSerializer.Deserialize<List<SicknessDocument>>(
                        JsonSerializer.Serialize(value, Json), Json)!))
            .IsRequired();

        // The two shapes every screen reads in: one person's history, and the
        // company's absences over a window.
        builder.HasIndex(record => new { record.EmployeeId, record.StartDate });

        builder.HasIndex(record => new { record.TenantId, record.StartDate });

        // The return-to-work queue.
        builder.HasIndex(record => new { record.TenantId, record.RtwStatus });
    }
}
