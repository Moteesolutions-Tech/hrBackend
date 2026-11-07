using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Organisation;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class EmploymentTypeConfigConfiguration
    : IEntityTypeConfiguration<EmploymentTypeConfig>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<EmploymentTypeConfig> builder)
    {
        builder.ToTable("employment_type_configs");

        builder.HasKey(type => type.Id);

        builder.Property(type => type.Name).HasMaxLength(100).IsRequired();
        builder.Property(type => type.Description).HasMaxLength(500);

        builder.Property(type => type.Kind).HasConversion<string>().HasMaxLength(20);

        builder.Property(type => type.PayFrequency)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(type => type.ContractDuration)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(type => type.HoursPerWeek).HasPrecision(6, 2);
        builder.Property(type => type.PensionEmployeePercent).HasPrecision(5, 2);
        builder.Property(type => type.PensionEmployerPercent).HasPrecision(5, 2);

        foreach (string property in (string[])
        [
            nameof(EmploymentTypeConfig.Benefits),
            nameof(EmploymentTypeConfig.StatutoryDeductionOverrides),
        ])
        {
            builder.Property<List<string>>(property)
                .HasColumnType("jsonb")
                .HasConversion(
                    value => JsonSerializer.Serialize(value, Json),
                    json => JsonSerializer.Deserialize<List<string>>(json, Json)!,
                    new ValueComparer<List<string>>(
                        (left, right) => JsonSerializer.Serialize(left, Json)
                            == JsonSerializer.Serialize(right, Json),
                        value => JsonSerializer.Serialize(value, Json).GetHashCode(),
                        value => JsonSerializer.Deserialize<List<string>>(
                            JsonSerializer.Serialize(value, Json), Json)!))
                .IsRequired();
        }

        // Two types with one name is two answers to "which is Sarah on".
        builder
            .HasIndex(type => new { type.TenantId, type.Name })
            .IsUnique()
            .HasDatabaseName("ix_employment_type_configs_one_per_name");

        // One row per built-in kind per tenant, so seeding twice cannot
        // double them up.
        builder
            .HasIndex(type => new { type.TenantId, type.Kind })
            .IsUnique()
            .HasFilter("kind IS NOT NULL")
            .HasDatabaseName("ix_employment_type_configs_one_per_kind");
    }
}
