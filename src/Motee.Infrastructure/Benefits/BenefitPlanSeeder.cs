using Motee.Domain.Benefits;
using Motee.Domain.Common;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Benefits;

// The benefits catalogue a new tenant starts with, plus the three coverage
// levels the plan form offers.
//
// Only the plans that mean something where the company operates: seeding both
// countries would leave a Nigerian tenant with fourteen rows about cycle-to-work
// and salary sacrifice to archive before their own catalogue was readable.
//
// Everything arrives as a draft, with no provider, no contribution rate and no
// waiting period. Those are terms of contracts this company may never have
// signed, and an HMO row that arrived active, naming an insurer and a 90-day
// qualifying period, would tell a new joiner they have cover nobody bought —
// the sort of error somebody only finds at a hospital reception desk. So the
// Draft tab is the list of what still needs a decision, and the hub honestly
// reports nobody as covered until somebody makes one.
internal sealed class BenefitPlanSeeder(MoteeDbContext dbContext, TimeProvider timeProvider)
{
    public void Seed(Guid tenantId, CountryCode country)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        int sequence = 0;

        foreach (string name in BenefitPlanSeeds.CoverageLevels)
        {
            dbContext.BenefitCoverageLevels.Add(new BenefitCoverageLevel
            {
                Id = Guid.NewGuid(),

                // Set explicitly: registration runs before any tenant is
                // current, so the context has nothing to stamp this from.
                TenantId = tenantId,
                Name = name,
                Sequence = sequence++,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        sequence = 0;

        foreach (BenefitPlanSeed seed in BenefitPlanSeeds.For(country))
        {
            dbContext.BenefitPlans.Add(new BenefitPlan
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SeedKey = seed.Key,
                Name = seed.Name,
                Category = seed.Category,
                Description = seed.Description,
                Scope = seed.Scope,
                EmploymentTypes = [.. seed.EmploymentTypes],
                Status = BenefitPlanStatus.Draft,
                Enrollment = seed.Enrollment,
                Country = seed.Country,
                Sequence = sequence++,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }
}
