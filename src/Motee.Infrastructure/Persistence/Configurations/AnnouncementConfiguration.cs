using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Announcements;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class AnnouncementConfiguration : IEntityTypeConfiguration<Announcement>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<Announcement> builder)
    {
        builder.ToTable("announcements");

        builder.HasKey(announcement => announcement.Id);

        builder.Property(announcement => announcement.Title).HasMaxLength(200).IsRequired();
        builder.Property(announcement => announcement.Body).IsRequired();

        builder.Property(announcement => announcement.Type)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(announcement => announcement.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(announcement => announcement.Priority)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(announcement => announcement.Audience)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // jsonb, like every other small id set here. It is written once and read
        // with the row; the audience is resolved in memory, so nothing ever asks
        // the database which announcements name a given department.
        //
        // The cost is that this column cannot be filtered on in SQL. That has
        // caught this codebase out before, so it is worth saying plainly rather
        // than discovering it at runtime.
        builder.Property(announcement => announcement.TargetDepartmentIds)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Json),
                json => JsonSerializer.Deserialize<List<Guid>>(json, Json)!,
                new ValueComparer<List<Guid>>(
                    (left, right) => JsonSerializer.Serialize(left, Json)
                        == JsonSerializer.Serialize(right, Json),
                    value => JsonSerializer.Serialize(value, Json).GetHashCode(),
                    value => JsonSerializer.Deserialize<List<Guid>>(
                        JsonSerializer.Serialize(value, Json), Json)!))
            .IsRequired();

        // The feed and the list both read this tenant's, newest first. Status is
        // deliberately absent: whether something is live is a date comparison,
        // not a value, so an index on status would not narrow the live set.
        builder.HasIndex(announcement => new { announcement.TenantId, announcement.CreatedAt });
    }
}

internal sealed class AnnouncementAcknowledgementConfiguration
    : IEntityTypeConfiguration<AnnouncementAcknowledgement>
{
    public void Configure(EntityTypeBuilder<AnnouncementAcknowledgement> builder)
    {
        builder.ToTable("announcement_acknowledgements");

        builder.HasKey(acknowledgement => acknowledgement.Id);

        // Declared so EF inserts the announcement before anything pointing at it.
        // Without the relationship the ordering is arbitrary and a create that
        // acknowledges in the same unit of work fails on the foreign key.
        builder.HasOne<Announcement>()
            .WithMany()
            .HasForeignKey(acknowledgement => acknowledgement.AnnouncementId)
            .OnDelete(DeleteBehavior.Cascade);

        // Agreeing twice is the same agreement, and two tabs are faster than a
        // read followed by a write.
        builder.HasIndex(acknowledgement =>
                new { acknowledgement.AnnouncementId, acknowledgement.EmployeeId })
            .IsUnique()
            .HasDatabaseName("ix_announcement_acknowledgements_one_per_person");
    }
}

internal sealed class AnnouncementViewConfiguration : IEntityTypeConfiguration<AnnouncementView>
{
    public void Configure(EntityTypeBuilder<AnnouncementView> builder)
    {
        builder.ToTable("announcement_views");

        builder.HasKey(view => view.Id);

        builder.HasOne<Announcement>()
            .WithMany()
            .HasForeignKey(view => view.AnnouncementId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per reader, which is what makes the count unique readers
        // rather than clicks.
        builder.HasIndex(view => new { view.AnnouncementId, view.EmployeeId })
            .IsUnique()
            .HasDatabaseName("ix_announcement_views_one_per_person");
    }
}
