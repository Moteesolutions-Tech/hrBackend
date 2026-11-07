using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Organisation;

// The eleven kinds of employment a new tenant starts with.
//
// Seeded because the rest of the product already maps onto them: an employee
// record, a vacancy and an onboarding all carry an employment type, so a
// company with none configured would have screens referring to types their
// own settings page says do not exist.
//
// Seeded as the tenant's own, not as system rows. Hours, pay frequency and
// probation are starting points every company edits; what they cannot do is
// delete one, because something else is pointing at it.
//
// Pension percentages are deliberately left blank. Nigeria's statutory
// minimum is 8% and 10%, and that is not every company's actual scheme — a
// seeded number that looks configured is worse than an empty field somebody
// has to fill in, because nobody checks a figure that is already there.
internal sealed class EmploymentTypeSeeder(MoteeDbContext dbContext, TimeProvider timeProvider)
{
    public void Seed(Guid tenantId)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        int sequence = 0;

        foreach (EmploymentTypeSeed seed in EmploymentTypeSeeds.All)
        {
            dbContext.EmploymentTypeConfigs.Add(new EmploymentTypeConfig
            {
                Id = Guid.NewGuid(),

                // Set explicitly: registration runs before any tenant is
                // current, so the context has nothing to stamp this from.
                TenantId = tenantId,
                Kind = seed.Kind,
                Name = seed.Name,
                Description = seed.Description,
                PayFrequency = seed.PayFrequency,
                ContractDuration = seed.ContractDuration,
                HoursPerWeek = seed.HoursPerWeek,
                ProbationMonths = seed.ProbationMonths,
                ProbationReviewRequired = seed.ProbationMonths > 0,
                EligibleForBenefits = seed.EligibleForBenefits,
                PayrollInclusion = true,
                IsActive = true,
                Sequence = sequence++,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }
}
