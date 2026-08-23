using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Common;
using Motee.Domain.Tenants;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    // Matches the API's enum handling so settings persist as "hierarchical" rather
    // than an ordinal, and rows written before StructureType became an enum still read.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");

        builder.HasKey(tenant => tenant.Id);

        builder.HasIndex(tenant => tenant.Slug).IsUnique();

        builder.Property(tenant => tenant.Name).HasMaxLength(200).IsRequired();
        builder.Property(tenant => tenant.Slug).HasMaxLength(100).IsRequired();
        builder.Property(tenant => tenant.Plan).HasMaxLength(50).IsRequired();
        builder.Property(tenant => tenant.Status).HasMaxLength(50).IsRequired();
        builder.Property(tenant => tenant.Industry).HasMaxLength(150);
        builder.Property(tenant => tenant.CompanySize)
            .HasConversion<string>()
            .HasMaxLength(50);
        builder.Property(tenant => tenant.CompanyEmailDomain).HasMaxLength(255);
        builder.Property(tenant => tenant.CompanyPolicies).HasMaxLength(20_000);

        builder.Property(tenant => tenant.Settings)
            .HasColumnType("jsonb")
            .HasConversion(
                settings => JsonSerializer.Serialize(settings, SerializerOptions),
                json => JsonSerializer.Deserialize<TenantSettings>(json, SerializerOptions)!,
                new ValueComparer<TenantSettings>(
                    (left, right) => JsonSerializer.Serialize(left, SerializerOptions)
                        == JsonSerializer.Serialize(right, SerializerOptions),
                    value => JsonSerializer.Serialize(value, SerializerOptions).GetHashCode(),
                    value => JsonSerializer.Deserialize<TenantSettings>(
                        JsonSerializer.Serialize(value, SerializerOptions), SerializerOptions)!))
            .IsRequired();
        builder.Property(tenant => tenant.LogoUrl).HasMaxLength(500);
        builder.Property(tenant => tenant.PrimaryColor).HasMaxLength(20);
        builder.Property(tenant => tenant.BillingEmail).HasMaxLength(320);

        // Persisted as the ISO alpha-2 string so the column is readable in SQL and
        // stable if the enum-like set grows.
        builder.Property(tenant => tenant.CountryCode)
            .HasConversion(
                code => code.Value,
                value => CountryCode.Parse(value))
            .HasMaxLength(2)
            .IsRequired();
    }
}
