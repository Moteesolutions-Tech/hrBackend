using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Auth;
using Motee.Domain.Settings;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class PickListOptionConfiguration : IEntityTypeConfiguration<PickListOption>
{
    public void Configure(EntityTypeBuilder<PickListOption> builder)
    {
        builder.ToTable("pick_list_options");

        builder.HasKey(option => option.Id);

        builder.Property(option => option.ListKey).HasMaxLength(60).IsRequired();
        builder.Property(option => option.Value)
            .HasMaxLength(PickListRules.MaximumValueLength)
            .IsRequired();

        builder.HasIndex(option => new { option.TenantId, option.ListKey, option.Sequence });
    }
}

internal sealed class NotificationPreferenceConfiguration
    : IEntityTypeConfiguration<NotificationPreference>
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    // Compared by serialising, like every other jsonb column here, which is why
    // the dictionary is replaced rather than mutated by the service: a mutation
    // in place leaves the comparer seeing no change and the save does nothing.
    private static readonly ValueComparer<Dictionary<string, ChannelChoice>> Comparer =
        new(
            (left, right) => JsonSerializer.Serialize(left, Options)
                == JsonSerializer.Serialize(right, Options),
            value => JsonSerializer.Serialize(value, Options).GetHashCode(),
            value => JsonSerializer.Deserialize<Dictionary<string, ChannelChoice>>(
                JsonSerializer.Serialize(value, Options), Options)!);

    public void Configure(EntityTypeBuilder<NotificationPreference> builder)
    {
        builder.ToTable("notification_preferences");

        builder.HasKey(preference => preference.Id);

        builder.Property(preference => preference.Groups)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Options),
                json => JsonSerializer.Deserialize<Dictionary<string, ChannelChoice>>(json, Options)!,
                Comparer)
            .IsRequired();

        builder.Property(preference => preference.DigestFrequency)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(preference => preference.TimeZoneId).HasMaxLength(100);
        builder.Property(preference => preference.Language).HasMaxLength(10);

        builder.HasIndex(preference => preference.UserId).IsUnique();
    }
}

internal sealed class PasswordHistoryConfiguration : IEntityTypeConfiguration<PasswordHistoryEntry>
{
    public void Configure(EntityTypeBuilder<PasswordHistoryEntry> builder)
    {
        builder.ToTable("password_history");

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.PasswordHash).IsRequired();

        builder.HasIndex(entry => new { entry.UserId, entry.CreatedAt });
    }
}
