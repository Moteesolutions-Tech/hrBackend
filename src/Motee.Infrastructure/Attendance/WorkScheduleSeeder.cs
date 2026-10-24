using Motee.Domain.Attendance;
using Motee.Domain.Common;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Attendance;

// The working week a new company starts with.
//
// Seeded, unlike departments, which are deliberately left empty. The difference is
// whether the answer is knowable: only the company knows which departments it has,
// while a standard working week has one obvious shape that is right for most of them
// and one edit away for the rest. Without it attendance has nothing to judge a punch
// against on day one — every row would read "present, lateness unknown".
//
// Hours differ by country for the same reason leave does. Nigeria's working day
// commonly runs 08:00–17:00 with an hour for lunch; the UK's 09:00–17:30 with the
// same. Neither is law — both are what the overwhelming majority of employers in each
// country actually do, which is what a default is for.
internal sealed class WorkScheduleSeeder(MoteeDbContext dbContext, TimeProvider timeProvider)
{
    public const string DefaultName = "Standard";

    public void Seed(Guid tenantId, CountryCode country)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        (TimeOnly start, TimeOnly end) = HoursFor(country);

        dbContext.WorkSchedules.Add(new WorkSchedule
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = DefaultName,
            Days = WorkSchedules.MondayToFriday(start, end),
            BreakMinutes = 60,
            GraceMinutes = 10,
            IsDefault = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private static (TimeOnly Start, TimeOnly End) HoursFor(CountryCode country) =>
        country == CountryCode.UnitedKingdom
            ? (new TimeOnly(9, 0), new TimeOnly(17, 30))
            : (new TimeOnly(8, 0), new TimeOnly(17, 0));
}
