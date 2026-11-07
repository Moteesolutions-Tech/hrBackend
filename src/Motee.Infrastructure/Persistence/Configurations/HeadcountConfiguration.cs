using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Headcount;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class HeadcountPlanConfiguration : IEntityTypeConfiguration<HeadcountPlan>
{
    public void Configure(EntityTypeBuilder<HeadcountPlan> builder)
    {
        builder.ToTable("headcount_plans");

        builder.HasKey(plan => plan.Id);

        builder.Property(plan => plan.Period).HasMaxLength(10).IsRequired();

        builder.Property(plan => plan.Budget).HasPrecision(18, 4);

        // One target per department per period. Two is two answers to one
        // question, and nothing downstream could choose between them.
        builder
            .HasIndex(plan => new { plan.DepartmentId, plan.Period })
            .IsUnique()
            .HasDatabaseName("ix_headcount_plans_one_per_period");

        // The board reads a whole period at a time.
        builder.HasIndex(plan => new { plan.TenantId, plan.Period });
    }
}
