using Microsoft.EntityFrameworkCore;
using Motee.Domain.Leave;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Leave;

// The company's closure days over a window, as a set the day count can ask.
//
// Fetched per request range rather than the whole table: a tenant three years into using
// this has hundreds of rows, and a fortnight's leave needs about two of them.
//
// Both methods take the employee's department, because a closure day can exempt one —
// the warehouse working through a bank holiday is charged for that day like any other.
// The exemption is tested in memory: it is a jsonb list, which cannot be filtered in
// SQL, and after the date range narrows there are a couple of rows to test.
internal sealed class HolidayLookup(MoteeDbContext dbContext)
{
    public async Task<IReadOnlySet<DateOnly>> DatesAsync(
        DateOnly from,
        DateOnly to,
        Guid? departmentId = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<(DateOnly Date, string Name)> closures =
            await NamedAsync(from, to, departmentId, cancellationToken);

        return closures.Select(closure => closure.Date).ToHashSet();
    }

    // With names, for the form that has to explain why ten calendar days cost six.
    public async Task<IReadOnlyList<(DateOnly Date, string Name)>> NamedAsync(
        DateOnly from,
        DateOnly to,
        Guid? departmentId = null,
        CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return [];
        }

        List<PublicHoliday> rows = await dbContext.PublicHolidays
            .AsNoTracking()
            .Where(holiday => holiday.Date >= from && holiday.Date <= to)
            .OrderBy(holiday => holiday.Date)
            .ToListAsync(cancellationToken);

        // Only the days that actually close the office for this person. An observance
        // the company marks but still works is not a day off, and a department exempt
        // from a closure is at its desk.
        return
        [
            .. rows
                .Where(holiday => holiday.Closes(departmentId))
                .Select(holiday => (holiday.Date, holiday.Name)),
        ];
    }
}
