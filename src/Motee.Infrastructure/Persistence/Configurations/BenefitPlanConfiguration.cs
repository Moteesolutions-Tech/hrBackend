using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Benefits;
using Motee.Domain.Common;
using Motee.Domain.Organisation;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class BenefitPlanConfiguration : IEntityTypeConfiguration<BenefitPlan>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<BenefitPlan> builder)
    {
        builder.ToTable("benefit_plans");

        builder.HasKey(plan => plan.Id);

        builder.Property(plan => plan.SeedKey).HasMaxLength(60);
        builder.Property(plan => plan.Name).HasMaxLength(100).IsRequired();
        builder.Property(plan => plan.Description).HasMaxLength(1000).IsRequired();
        builder.Property(plan => plan.Provider).HasMaxLength(200);
        builder.Property(plan => plan.CoverageDetails).HasMaxLength(500);
        builder.Property(plan => plan.CostNote).HasMaxLength(200);

        builder.Property(plan => plan.Category)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(plan => plan.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(plan => plan.Enrollment)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(plan => plan.Scope)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(plan => plan.EmployerContributionPercent).HasPrecision(5, 2);
        builder.Property(plan => plan.EmployeeContributionPercent).HasPrecision(5, 2);

        // CountryCode is a record struct, not an enum, so the conversion has to
        // be spelled out. HasConversion<string>() compiles and then leaves the
        // whole model unmappable — which the fixture used to report as every
        // integration test skipping.
        builder.Property(plan => plan.Country)
            .HasConversion(
                code => code!.Value.Value,
                value => CountryCode.Parse(value))
            .HasMaxLength(2);

        // The types a restricted plan covers. jsonb because it is read with the
        // row and never queried across — the scope filter runs in memory, which
        // is what the service's comment about Postgres is about.
        builder.Property(plan => plan.EmploymentTypes)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Json),
                json => JsonSerializer.Deserialize<List<EmploymentType>>(json, Json)!,
                new ValueComparer<List<EmploymentType>>(
                    (left, right) => JsonSerializer.Serialize(left, Json)
                        == JsonSerializer.Serialize(right, Json),
                    value => JsonSerializer.Serialize(value, Json).GetHashCode(),
                    value => JsonSerializer.Deserialize<List<EmploymentType>>(
                        JsonSerializer.Serialize(value, Json), Json)!))
            .IsRequired();

        // Cleared rather than cascaded: a plan whose coverage level was removed
        // keeps everything else it said.
        builder
            .HasOne<BenefitCoverageLevel>()
            .WithMany()
            .HasForeignKey(plan => plan.CoverageLevelId)
            .OnDelete(DeleteBehavior.SetNull);

        // One row per catalogue plan per tenant, so seeding twice cannot double
        // the catalogue up.
        builder
            .HasIndex(plan => new { plan.TenantId, plan.SeedKey })
            .IsUnique()
            .HasFilter("seed_key IS NOT NULL")
            .HasDatabaseName("ix_benefit_plans_one_per_seed_key");

        builder
            .HasIndex(plan => new { plan.TenantId, plan.Status })
            .HasDatabaseName("ix_benefit_plans_tenant_id_status");
    }
}

internal sealed class BenefitCoverageLevelConfiguration
    : IEntityTypeConfiguration<BenefitCoverageLevel>
{
    public void Configure(EntityTypeBuilder<BenefitCoverageLevel> builder)
    {
        builder.ToTable("benefit_coverage_levels");

        builder.HasKey(level => level.Id);

        builder.Property(level => level.Name).HasMaxLength(60).IsRequired();

        // Two levels with one name is two answers to which cover somebody is on.
        builder
            .HasIndex(level => new { level.TenantId, level.Name })
            .IsUnique()
            .HasDatabaseName("ix_benefit_coverage_levels_one_per_name");
    }
}
