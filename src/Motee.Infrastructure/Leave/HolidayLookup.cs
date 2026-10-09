using Microsoft.EntityFrameworkCore;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Leave;

// The company's closure days over a window, as a set the day count can ask.
//
// Fetched per request range rather than the whole table: a tenant three years into using
// this has hundreds of rows, and a fortnight's leave needs about two of them.
internal sealed class HolidayLookup(MoteeDbContext dbContext)
{
    public async Task<IReadOnlySet<DateOnly>> DatesAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return new HashSet<DateOnly>();
        }

        List<DateOnly> dates = await dbContext.PublicHolidays
            .AsNoTracking()
            .Where(holiday => holiday.Date >= from && holiday.Date <= to)
            .Select(holiday => holiday.Date)
            .ToListAsync(cancellationToken);

        return dates.ToHashSet();
    }

    // With names, for the form that has to explain why ten calendar days cost six.
    public async Task<IReadOnlyList<(DateOnly Date, string Name)>> NamedAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return [];
        }

        List<PublicHolidayRow> rows = await dbContext.PublicHolidays
            .AsNoTracking()
            .Where(holiday => holiday.Date >= from && holiday.Date <= to)
            .OrderBy(holiday => holiday.Date)
            .Select(holiday => new PublicHolidayRow(holiday.Date, holiday.Name))
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => (row.Date, row.Name))];
    }

    private sealed record PublicHolidayRow(DateOnly Date, string Name);
}
