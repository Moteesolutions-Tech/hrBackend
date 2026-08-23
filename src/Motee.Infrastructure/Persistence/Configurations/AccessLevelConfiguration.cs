using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Authorization;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class AccessLevelConfiguration : IEntityTypeConfiguration<AccessLevel>
{
    // Enums as their names, so a permission set is readable in psql and survives an
    // enum being reordered — the same reason every other enum here is stored as text.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public void Configure(EntityTypeBuilder<AccessLevel> builder)
    {
        builder.ToTable("access_levels");

        builder.HasKey(level => level.Id);

        builder.Property(level => level.TemplateSlug).HasMaxLength(50);
        builder.Property(level => level.Name).HasMaxLength(100).IsRequired();
        builder.Property(level => level.Description).HasMaxLength(500);
        builder.Property(level => level.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(level => level.Status).HasConversion<string>().HasMaxLength(20);

        // jsonb rather than tables. A permission set is read whole, written whole and
        // never queried by its parts — "which levels grant Export on employees" is a
        // question for an admin screen over a handful of rows, not an index.
        AsJsonb(builder.Property(level => level.Permissions));
        AsJsonb(builder.Property(level => level.Scope));

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(level => level.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // Two levels in one tenant cannot share a name: an admin picking from a list
        // of three "HR Manager" entries has no way to choose correctly.
        builder.HasIndex(level => new { level.TenantId, level.Name })
            .HasDatabaseName("ix_access_levels_tenant_name")
            .IsUnique();

        // Registration looks the seeded levels up by slug to assign the founder.
        builder.HasIndex(level => new { level.TenantId, level.TemplateSlug })
            .HasDatabaseName("ix_access_levels_tenant_template")
            .IsUnique()
            .HasFilter("template_slug IS NOT NULL");
    }

    private static void AsJsonb<T>(PropertyBuilder<T> property) =>
        property
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Json),
                json => JsonSerializer.Deserialize<T>(json, Json)!,
                new ValueComparer<T>(
                    (left, right) => JsonSerializer.Serialize(left, Json)
                        == JsonSerializer.Serialize(right, Json),
                    value => JsonSerializer.Serialize(value, Json).GetHashCode(),
                    value => JsonSerializer.Deserialize<T>(
                        JsonSerializer.Serialize(value, Json), Json)!))
            .IsRequired();
}

internal sealed class UserAccessLevelConfiguration : IEntityTypeConfiguration<UserAccessLevel>
{
    public void Configure(EntityTypeBuilder<UserAccessLevel> builder)
    {
        builder.ToTable("user_access_levels");

        builder.HasKey(assignment => assignment.Id);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(assignment => assignment.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(assignment => assignment.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, not cascade: deleting a level that people hold would silently
        // strip their access. The service refuses the delete instead and offers
        // deactivation, which is visible.
        builder.HasOne<AccessLevel>()
            .WithMany()
            .HasForeignKey(assignment => assignment.AccessLevelId)
            .OnDelete(DeleteBehavior.Restrict);

        // Holding the same level twice is not a second grant, it is a duplicate row
        // that makes the merge count it twice for no effect and confuses the audit.
        builder.HasIndex(assignment => new { assignment.UserId, assignment.AccessLevelId })
            .HasDatabaseName("ix_user_access_levels_user_level")
            .IsUnique();

        // Every authorized request resolves what a user holds.
        builder.HasIndex(assignment => assignment.UserId)
            .HasDatabaseName("ix_user_access_levels_user");
    }
}

internal sealed class BusinessUnitConfiguration : IEntityTypeConfiguration<BusinessUnit>
{
    public void Configure(EntityTypeBuilder<BusinessUnit> builder)
    {
        builder.ToTable("business_units");

        builder.HasKey(unit => unit.Id);

        builder.Property(unit => unit.Name).HasMaxLength(150).IsRequired();
        builder.Property(unit => unit.Code).HasMaxLength(50);
        builder.Property(unit => unit.Description).HasMaxLength(500);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(unit => unit.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(unit => new { unit.TenantId, unit.Name })
            .HasDatabaseName("ix_business_units_tenant_name")
            .IsUnique();
    }
}
