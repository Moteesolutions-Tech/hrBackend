using Motee.Domain.Checklists;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Checklists;

// The onboarding template a new tenant starts with.
//
// Seeded live, unlike the benefits catalogue: these are process steps every
// employer does, and none of them asserts a provider, a contract or a figure
// that could be wrong. The worst case is a company retiring a task it does
// differently, which is one click — whereas a blank template makes the screen
// useless on the day somebody first opens it.
internal sealed class ChecklistSeeder(MoteeDbContext dbContext, TimeProvider timeProvider)
{
    public void Seed(Guid tenantId)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        int sequence = 0;

        foreach (ChecklistSeed seed in ChecklistSeeds.All)
        {
            dbContext.ChecklistItems.Add(new ChecklistItem
            {
                Id = Guid.NewGuid(),

                // Set explicitly: registration runs before any tenant is
                // current, so the context has nothing to stamp this from.
                TenantId = tenantId,
                Title = seed.Title,
                Description = seed.Description,
                Category = seed.Category,
                ResponsibleParty = seed.ResponsibleParty,
                DueDayOffset = seed.DueDayOffset,
                IsRequired = seed.IsRequired,
                IsActive = true,
                Sequence = sequence++,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }
}
