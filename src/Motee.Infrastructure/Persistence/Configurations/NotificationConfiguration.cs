using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Notifications;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");

        builder.HasKey(notification => notification.Id);

        builder.Property(notification => notification.Kind)
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(notification => notification.Severity)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(notification => notification.Title).HasMaxLength(200).IsRequired();
        builder.Property(notification => notification.Body).HasMaxLength(1000).IsRequired();
        builder.Property(notification => notification.Href).HasMaxLength(500);
        builder.Property(notification => notification.SubjectType).HasMaxLength(50);

        // The index the whole design rests on: one notification per person per
        // kind per subject. It is what lets a sweep run every night for ninety
        // nights and leave one row rather than ninety.
        //
        // Partial, because a notification about nothing in particular — a
        // broadcast with no subject — is not a duplicate of another one.
        builder.HasIndex(notification =>
                new { notification.TenantId, notification.UserId, notification.Kind, notification.SubjectId })
            .IsUnique()
            .HasFilter("subject_id IS NOT NULL")
            .HasDatabaseName("ix_notifications_one_per_subject");

        // The panel's read: mine, newest first, undismissed.
        builder.HasIndex(notification => new { notification.UserId, notification.CreatedAt });
    }
}
