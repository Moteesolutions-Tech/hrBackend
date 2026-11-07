using Microsoft.EntityFrameworkCore;
using Motee.Application.Tenancy;
using Motee.Application.Workforce;
using Motee.Domain.Employees;
using Motee.Domain.Headcount;
using Motee.Domain.Offboarding;
using Motee.Domain.Recruitment;
using Motee.Domain.Tenants;
using Motee.Domain.Workforce;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Workforce;

// Turnover and hiring, worked out from the records that already hold them.
//
// Nothing is stored. The screen these feed held three hardcoded arrays, and
// the point of replacing them is that these figures move when the underlying
// records do — a resignation logged this morning is in this afternoon's
// turnover rate without anything having to recompute.
internal sealed class WorkforceAnalyticsService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : IWorkforceAnalyticsService
{
    // Two months. The point at which a vacancy has stopped being a search and
    // become a problem.
    private const int StaleAfterDays = 61;

    private static readonly EmployeeStatus[] InPost =
    [
        EmployeeStatus.Probation,
        EmployeeStatus.Active,
        EmployeeStatus.OnLeave,
        EmployeeStatus.Offboarding,
    ];

    private List<Person>? _people;

    private Dictionary<Guid, string>? _departments;

    private string? _zone;

    public async Task<WorkforceOverviewDto> OverviewAsync(
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        List<Person> people = await PeopleAsync(cancellationToken);

        List<Person> inPost =
            [.. people.Where(person => InPost.Contains(person.Status) && person.Spell.Start is not null)];

        DateOnly yearAgo = today.AddYears(-1);

        List<Exit> exits = await ExitsBetweenAsync(yearAgo, today, null, cancellationToken);

        int opening = Headcounts.OnDay([.. people.Select(person => person.Spell)], yearAgo);

        TurnoverTally tally = Turnover.Of(
            exits, Turnover.AverageHeadcount(opening, inPost.Count));

        return new WorkforceOverviewDto
        {
            AsOf = today,
            Headcount = inPost.Count,
            AverageTenureYears = inPost.Count == 0
                ? 0m
                : Math.Round(
                    inPost.Sum(person =>
                        (today.DayNumber - person.Spell.Start!.Value.DayNumber) / 365.25m)
                        / inPost.Count,
                    1,
                    MidpointRounding.AwayFromZero),
            OpenVacancies = await OpenVacanciesAsync(null, cancellationToken),
            TurnoverRatePercent = tally.RatePercent,
            LeaversLastTwelveMonths = tally.Total,
        };
    }

    public async Task<IReadOnlyList<TurnoverRowDto>> TurnoverAsync(
        TurnoverQuery query,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        int year = query.Year ?? today.Year;

        List<Person> people = await PeopleAsync(cancellationToken);
        IReadOnlyDictionary<Guid, string> departments = await DepartmentsAsync(cancellationToken);

        List<TurnoverRowDto> rows = [];

        foreach (string period in PlanPeriods.QuartersOf(year))
        {
            if (PlanPeriods.Window(period) is not (DateOnly from, DateOnly to))
            {
                continue;
            }

            // A quarter still running is measured to today rather than to its
            // own end, or its rate reads as a fraction of a quarter nobody has
            // lived through yet.
            DateOnly closes = to < today ? to : today;

            if (from > today)
            {
                continue;
            }

            foreach ((Guid? departmentId, string? name) in Scope(departments, query.DepartmentId))
            {
                IReadOnlyCollection<Spell> spells =
                [
                    .. people
                        .Where(person => departmentId is null || person.DepartmentId == departmentId)
                        .Select(person => person.Spell),
                ];

                int average = Turnover.AverageHeadcount(
                    Headcounts.OnDay(spells, from), Headcounts.OnDay(spells, closes));

                TurnoverTally tally = Turnover.Of(
                    await ExitsBetweenAsync(from, closes, departmentId, cancellationToken),
                    average);

                rows.Add(new TurnoverRowDto
                {
                    Period = period,
                    DepartmentId = departmentId,
                    DepartmentName = name,
                    AverageHeadcount = average,
                    Leavers = tally.Total,
                    Voluntary = tally.Voluntary,
                    Involuntary = tally.Involuntary,
                    Unclassified = tally.Unclassified,
                    RatePercent = tally.RatePercent,
                });
            }
        }

        return rows;
    }

    // The company row first, then each department. A null id is the company,
    // so a reader can tell the total from a department that happens to have
    // no name.
    private static IEnumerable<(Guid? Id, string? Name)> Scope(
        IReadOnlyDictionary<Guid, string> departments,
        Guid? only)
    {
        if (only is Guid wanted)
        {
            yield return (wanted, departments.GetValueOrDefault(wanted));
            yield break;
        }

        yield return (null, null);

        foreach ((Guid id, string name) in departments
            .OrderBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase))
        {
            yield return (id, name);
        }
    }

    public async Task<IReadOnlyList<HiringRowDto>> HiringAsync(
        HiringQuery query,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        DateOnly since = today.AddMonths(-Math.Max(1, query.Months));
        DateOnly quarterStart = PlanPeriods.Window(PlanPeriods.QuarterOf(today))!.Value.Start;

        IReadOnlyDictionary<Guid, string> departments = await DepartmentsAsync(cancellationToken);

        List<Vacancy> vacancies = await dbContext.JobRequisitions
            .AsNoTracking()
            .Where(requisition => requisition.DepartmentId != null)
            .Select(requisition => new Vacancy(
                requisition.Id,
                requisition.DepartmentId!.Value,
                requisition.Status,
                requisition.Openings,
                requisition.PublishedAt,
                requisition.ClosedAt))
            .ToListAsync(cancellationToken);

        List<Guid> ids = [.. vacancies.Select(vacancy => vacancy.Id)];

        // Offers, with the acceptance that filled the role. Read once for
        // every vacancy rather than per department.
        List<Offer> offers = ids.Count == 0
            ? []
            : await dbContext.CandidateOffers
                .AsNoTracking()
                .Join(
                    dbContext.Candidates.AsNoTracking().Where(c => ids.Contains(c.RequisitionId)),
                    offer => offer.CandidateId,
                    candidate => candidate.Id,
                    (offer, candidate) => new Offer(
                        candidate.RequisitionId, offer.Status, offer.SentAt, offer.RespondedAt))
                .ToListAsync(cancellationToken);

        List<HiringRowDto> rows = [];

        foreach ((Guid departmentId, string name) in departments
            .Where(entry => query.DepartmentId is null || entry.Key == query.DepartmentId)
            .OrderBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase))
        {
            List<Vacancy> theirs =
                [.. vacancies.Where(vacancy => vacancy.DepartmentId == departmentId)];

            HashSet<Guid> theirIds = [.. theirs.Select(vacancy => vacancy.Id)];

            List<Offer> theirOffers =
                [.. offers.Where(offer => theirIds.Contains(offer.RequisitionId))];

            List<Vacancy> recruiting =
                [.. theirs.Where(vacancy => Requisitions.IsRecruiting(vacancy.Status))];

            // Opened to accepted, for the roles that actually got filled
            // inside the window.
            List<Filled> filled =
            [
                .. theirOffers
                    .Where(offer => offer.Status == OfferStatus.Accepted
                        && offer.RespondedAt is not null)
                    .Select(offer => new
                    {
                        Opened = OpenedOn(theirs, offer.RequisitionId, offer.SentAt),
                        Accepted = DateOnly.FromDateTime(offer.RespondedAt!.Value.UtcDateTime),
                    })
                    .Where(role => role.Accepted >= since)
                    .Select(role => new Filled(role.Opened, role.Accepted)),
            ];

            List<int> openFor =
            [
                .. recruiting
                    .Where(vacancy => vacancy.PublishedAt is not null)
                    .Select(vacancy => Hiring.DaysOpen(
                        DateOnly.FromDateTime(vacancy.PublishedAt!.Value.UtcDateTime), today)),
            ];

            int extended = theirOffers.Count(offer => offer.SentAt >= Instant(since));
            int accepted = theirOffers.Count(offer =>
                offer.Status == OfferStatus.Accepted && offer.SentAt >= Instant(since));

            rows.Add(new HiringRowDto
            {
                DepartmentId = departmentId,
                DepartmentName = name,
                OpenRequisitions = recruiting.Count,

                // Openings still to fill, not requisitions: one requisition
                // for three engineers is three openings.
                OpenVacancies = recruiting.Sum(vacancy => Math.Max(1, vacancy.Openings)),
                AverageDaysToFill = Hiring.AverageDaysToFill(filled),
                OffersExtended = extended,
                OffersAccepted = accepted,
                AcceptanceRatePercent = Hiring.AcceptanceRate(extended, accepted),
                FilledThisQuarter = filled.Count(role => role.AcceptedOn >= quarterStart),
                StaleVacancies = openFor.Count(days => Hiring.IsStale(days, StaleAfterDays)),
                LongestOpenDays = openFor.Count == 0 ? 0 : openFor.Max(),
            });
        }

        return rows;
    }

    // When the role opened. The publication date where there is one, falling
    // back to the day the offer went out — a vacancy filled without ever
    // being published took no measurable time to fill, which is truer than
    // dating it from a null.
    private static DateOnly OpenedOn(
        IReadOnlyList<Vacancy> vacancies,
        Guid requisitionId,
        DateTimeOffset offerSentAt)
    {
        Vacancy? vacancy = vacancies.FirstOrDefault(row => row.Id == requisitionId);

        return vacancy?.PublishedAt is DateTimeOffset published
            ? DateOnly.FromDateTime(published.UtcDateTime)
            : DateOnly.FromDateTime(offerSentAt.UtcDateTime);
    }

    // Exits in a window, by last working date where offboarding recorded one
    // and by the employee's leaving date otherwise. Offboarding holds the
    // reason, which is the only thing that can split the figure.
    private async Task<List<Exit>> ExitsBetweenAsync(
        DateOnly from,
        DateOnly to,
        Guid? departmentId,
        CancellationToken cancellationToken)
    {
        List<Person> people = await PeopleAsync(cancellationToken);

        List<Person> left =
        [
            .. people.Where(person =>
                person.Spell.End is DateOnly end
                && end >= from
                && end <= to
                && (departmentId is null || person.DepartmentId == departmentId)),
        ];

        if (left.Count == 0)
        {
            return [];
        }

        List<Guid> ids = [.. left.Select(person => person.Id)];

        Dictionary<Guid, ExitReason> reasons = await dbContext.OffboardingRecords
            .AsNoTracking()
            .Where(record => ids.Contains(record.EmployeeId))
            .GroupBy(record => record.EmployeeId)
            .Select(group => group.OrderByDescending(record => record.InitiatedAt).First())
            .ToDictionaryAsync(
                record => record.EmployeeId, record => record.ExitReason, cancellationToken);

        return
        [
            .. left.Select(person => new Exit(
                reasons.TryGetValue(person.Id, out ExitReason reason) ? reason : null)),
        ];
    }

    private async Task<int> OpenVacanciesAsync(
        Guid? departmentId,
        CancellationToken cancellationToken)
    {
        List<Vacancy> recruiting = await dbContext.JobRequisitions
            .AsNoTracking()
            .Where(requisition =>
                (requisition.Status == RequisitionStatus.Open
                    || requisition.Status == RequisitionStatus.Interviewing
                    || requisition.Status == RequisitionStatus.OfferStage)
                && (departmentId == null || requisition.DepartmentId == departmentId))
            .Select(requisition => new Vacancy(
                requisition.Id,
                requisition.DepartmentId ?? Guid.Empty,
                requisition.Status,
                requisition.Openings,
                requisition.PublishedAt,
                requisition.ClosedAt))
            .ToListAsync(cancellationToken);

        return recruiting.Sum(vacancy => Math.Max(1, vacancy.Openings));
    }

    private static DateTimeOffset Instant(DateOnly day) =>
        new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private async Task<List<Person>> PeopleAsync(CancellationToken cancellationToken)
    {
        _people ??= await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Status != EmployeeStatus.Deleted)
            .Select(employee => new Person(
                employee.Id,
                employee.Status,
                employee.DepartmentId,
                new Spell(employee.StartDate, employee.DateOfLeaving)))
            .ToListAsync(cancellationToken);

        return _people;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> DepartmentsAsync(
        CancellationToken cancellationToken)
    {
        _departments ??= await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id, department => department.Name, cancellationToken);

        return _departments;
    }

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken) =>
        TenantClock.DayOf(timeProvider.GetUtcNow(), await ZoneAsync(cancellationToken));

    private async Task<string?> ZoneAsync(CancellationToken cancellationToken)
    {
        if (_zone is not null)
        {
            return _zone;
        }

        if (currentTenant.TenantId is not Guid tenantId)
        {
            return null;
        }

        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        _zone = settings?.TimeZoneId;

        return _zone;
    }

    private sealed record Person(
        Guid Id,
        EmployeeStatus Status,
        Guid? DepartmentId,
        Spell Spell);

    private sealed record Vacancy(
        Guid Id,
        Guid DepartmentId,
        RequisitionStatus Status,
        int Openings,
        DateTimeOffset? PublishedAt,
        DateTimeOffset? ClosedAt);

    private sealed record Offer(
        Guid RequisitionId,
        OfferStatus Status,
        DateTimeOffset SentAt,
        DateTimeOffset? RespondedAt);
}
