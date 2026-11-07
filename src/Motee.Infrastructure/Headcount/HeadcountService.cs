using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Headcount;
using Motee.Application.Tenancy;
using Motee.Domain.Employees;
using Motee.Domain.Headcount;
using Motee.Domain.Organisation;
using Motee.Domain.Performance;
using Motee.Domain.Recruitment;
using Motee.Domain.Sickness;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Headcount;

// This file's own namespace shadows the domain's helper, so the rules it
// holds are aliased rather than written out in full at each call.
using Rules = Motee.Domain.Headcount.Headcounts;

// Headcount planning: one stored target per department per period, and
// everything else worked out from the records that already exist.
//
// The actual headcount is the part worth reading carefully. A period's figure
// is counted as at the last day of that period, from start and leaving dates
// — so Q1's number is who was there in Q1, not today's roster scaled by a
// guess. A future period counts as at today, because a target for next
// quarter against a headcount nobody has yet would read as a total shortfall.
internal sealed class HeadcountService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IHeadcountService
{
    // Shared with benefits, which has to report the same workforce off the
    // same table.
    private static readonly EmployeeStatus[] InPost = EmployeeLifecycle.InPost;

    private List<Person>? _people;

    private Dictionary<Guid, string>? _departments;

    private string? _zone;

    // ── the board ───────────────────────────────────────────────────────────

    public async Task<HeadcountPlanBoardDto> BoardAsync(
        string? period = null,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        string wanted = PlanPeriods.IsValid(period) ? period! : PlanPeriods.QuarterOf(today);

        DateOnly countedAsAt = CountedAsAt(wanted, today);
        string? previous = PlanPeriods.PreviousQuarter(wanted);

        IReadOnlyDictionary<Guid, string> departments = await DepartmentsAsync(cancellationToken);
        List<Person> people = await PeopleAsync(cancellationToken);

        Dictionary<Guid, HeadcountPlan> plans = await PlansForAsync(wanted, cancellationToken);

        Dictionary<Guid, HeadcountPlan> before = previous is null
            ? []
            : await PlansForAsync(previous, cancellationToken);

        DateOnly? beforeAsAt = previous is null ? null : CountedAsAt(previous, today);

        IReadOnlyDictionary<Guid, Pipeline> pipelines =
            await PipelinesAsync(today, cancellationToken);

        List<HeadcountPlanRowDto> rows = [];

        foreach ((Guid departmentId, string name) in departments
            .OrderBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase))
        {
            int actual = Rules.OnDay(SpellsIn(people, departmentId), countedAsAt);

            Pipeline pipeline = pipelines.GetValueOrDefault(departmentId, Pipeline.Empty);
            HeadcountPlan? plan = plans.GetValueOrDefault(departmentId);

            int? previousGap = beforeAsAt is DateOnly was
                && before.TryGetValue(departmentId, out HeadcountPlan? earlier)
                ? Rules.OnDay(SpellsIn(people, departmentId), was) - earlier.Target
                : null;

            rows.Add(new HeadcountPlanRowDto
            {
                Id = plan?.Id,
                DepartmentId = departmentId,
                DepartmentName = name,
                Period = wanted,
                Target = plan?.Target,
                Actual = actual,
                Gap = plan is null ? null : actual - plan.Target,
                GapStatus = plan is null ? null : Rules.Gap(actual, plan.Target),
                Budget = plan?.Budget,
                OpenVacancies = pipeline.OpenVacancies,
                ActiveRecruitments = pipeline.ActiveRecruitments,
                RecruitmentStage = pipeline.Stage,
                AcceptedNotStarted = pipeline.AcceptedNotStarted,
                ExpectedStartDate = pipeline.ExpectedStartDate,
                PreviousPeriodGap = previousGap,

                // Unplanned departments get the advice for a department on
                // target: there is no gap to act on until somebody sets one.
                RecommendedAction = Rules.RecommendedAction(
                    actual,
                    plan?.Target ?? actual,
                    pipeline.ActiveRecruitments,
                    pipeline.OpenVacancies),
            });
        }

        return new HeadcountPlanBoardDto
        {
            Period = wanted,
            CountedAsAt = countedAsAt,
            Rows = rows,
            TotalTarget = rows.Sum(row => row.Target ?? 0),
            TotalActual = rows.Sum(row => row.Actual),
            TotalGap = rows.Sum(row => row.Gap ?? 0),
            DepartmentsUnder = rows.Count(row => row.GapStatus == GapStatus.Under),
            DepartmentsOver = rows.Count(row => row.GapStatus == GapStatus.Over),
            DepartmentsOnTarget = rows.Count(row => row.GapStatus == GapStatus.OnTarget),
            DepartmentsUnplanned = rows.Count(row => row.Target is null),
            TotalBudget = rows.Any(row => row.Budget is not null)
                ? rows.Sum(row => row.Budget ?? 0m)
                : null,
        };
    }

    // The last day of the period, except for one still to come: a target for
    // next quarter against a headcount nobody has yet is not a shortfall.
    private static DateOnly CountedAsAt(string period, DateOnly today) =>
        PlanPeriods.Window(period) is (DateOnly _, DateOnly end) && end < today ? end : today;

    // ── trends ──────────────────────────────────────────────────────────────

    public async Task<HeadcountTrendDto> TrendAsync(
        string period,
        TrendComparison comparison = TrendComparison.PreviousQuarter,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        string wanted = PlanPeriods.IsValid(period) ? period : PlanPeriods.QuarterOf(today);

        string? against = comparison == TrendComparison.SameQuarterLastYear
            ? PlanPeriods.SameQuarterLastYear(wanted)
            : PlanPeriods.PreviousQuarter(wanted);

        IReadOnlyDictionary<Guid, string> departments = await DepartmentsAsync(cancellationToken);
        List<Person> people = await PeopleAsync(cancellationToken);

        Dictionary<Guid, HeadcountPlan> plans = await PlansForAsync(wanted, cancellationToken);

        DateOnly now = CountedAsAt(wanted, today);
        DateOnly? then = against is null ? null : CountedAsAt(against, today);

        List<HeadcountTrendRowDto> rows =
        [
            .. departments
                .OrderBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase)
                .Select(entry =>
                {
                    IReadOnlyCollection<Spell> spells = SpellsIn(people, entry.Key);

                    int current = Rules.OnDay(spells, now);
                    int previous = then is DateOnly was ? Rules.OnDay(spells, was) : 0;

                    return new HeadcountTrendRowDto
                    {
                        DepartmentId = entry.Key,
                        DepartmentName = entry.Value,
                        Current = current,
                        Previous = previous,
                        Delta = current - previous,
                        PercentChange = Rules.PercentChange(current, previous),
                        Target = plans.GetValueOrDefault(entry.Key)?.Target,
                    };
                }),
        ];

        int totalCurrent = rows.Sum(row => row.Current);
        int totalPrevious = rows.Sum(row => row.Previous);

        return new HeadcountTrendDto
        {
            Period = wanted,
            ComparisonPeriod = against,
            Rows = rows,
            TotalCurrent = totalCurrent,
            TotalPrevious = totalPrevious,
            TotalDelta = totalCurrent - totalPrevious,
            TotalPercentChange = Rules.PercentChange(totalCurrent, totalPrevious),
            Grew = rows.Count(row => row.Delta > 0),
            Shrank = rows.Count(row => row.Delta < 0),
            Flat = rows.Count(row => row.Delta == 0),
        };
    }

    public async Task<IReadOnlyList<HeadcountSeriesPointDto>> SeriesAsync(
        int? year = null,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        int wanted = year ?? today.Year;

        List<Person> people = await PeopleAsync(cancellationToken);
        IReadOnlyCollection<Spell> spells = [.. people.Select(person => person.Spell)];

        List<HeadcountSeriesPointDto> points = [];

        foreach (string period in PlanPeriods.QuartersOf(wanted))
        {
            Dictionary<Guid, HeadcountPlan> plans =
                await PlansForAsync(period, cancellationToken);

            points.Add(new HeadcountSeriesPointDto
            {
                Period = period,
                Actual = Rules.OnDay(spells, CountedAsAt(period, today)),

                // Null, not zero. A quarter nobody planned is not a quarter
                // planned at nothing, and a chart drawing it as zero invents a
                // shortfall the size of the company.
                Target = plans.Count == 0 ? null : plans.Values.Sum(plan => plan.Target),
            });
        }

        return points;
    }

    // ── workforce composition ───────────────────────────────────────────────

    public async Task<DemographicsDto> DemographicsAsync(
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        List<Person> people = await PeopleAsync(cancellationToken);
        IReadOnlyDictionary<Guid, string> departments = await DepartmentsAsync(cancellationToken);

        Dictionary<Guid, string> branches = await dbContext.Branches
            .AsNoTracking()
            .ToDictionaryAsync(branch => branch.Id, branch => branch.Name, cancellationToken);

        List<Person> inPost =
            [.. people.Where(person => InPost.Contains(person.Status) && person.Spell.Start is not null)];

        int total = inPost.Count;

        return new DemographicsDto
        {
            AsOf = today,
            Headcount = total,
            Department = Tally(
                inPost,
                person => person.DepartmentId is Guid id
                    ? departments.GetValueOrDefault(id)
                    : null,
                total,
                person => person.DepartmentId,
                null),
            EmploymentType = Tally(
                inPost, person => person.EmploymentType?.ToString(), total, null, null),
            Status = Tally(inPost, person => person.Status.ToString(), total, null, null),
            Tenure = Tally(
                inPost,
                person => person.Spell.Start is DateOnly started
                    ? Rules.TenureBand(started, today)
                    : null,
                total,
                null,
                null),

            // Bands, never an age.
            Age = Tally(
                inPost,
                person => person.DateOfBirth is DateOnly born
                    ? Rules.AgeBand(born, today)
                    : null,
                total,
                null,
                null),
            Gender = Tally(inPost, person => person.Gender, total, null, null),
            Grade = Tally(inPost, person => person.Grade, total, null, null),
            Location = Tally(
                inPost,
                person => person.BranchId is Guid id
                    ? branches.GetValueOrDefault(id)
                    : person.WorkLocation,
                total,
                null,
                person => person.BranchId),
        };
    }

    // Counts by category, biggest first. A blank field is left out rather
    // than bucketed: an "Unknown" slice on a diversity chart is a category
    // nobody chose, and it gets read as one.
    private static IReadOnlyList<DemographicItemDto> Tally(
        IReadOnlyList<Person> people,
        Func<Person, string?> categorise,
        int total,
        Func<Person, Guid?>? departmentOf,
        Func<Person, Guid?>? branchOf) =>
        [
            .. people
                .Select(person => new
                {
                    Label = categorise(person)?.Trim(),
                    DepartmentId = departmentOf?.Invoke(person),
                    BranchId = branchOf?.Invoke(person),
                })
                .Where(row => !string.IsNullOrWhiteSpace(row.Label))
                .GroupBy(row => row.Label!, StringComparer.Ordinal)
                .Select(group => new DemographicItemDto
                {
                    Label = group.Key,
                    Count = group.Count(),
                    Percentage = total == 0
                        ? 0m
                        : Math.Round(
                            group.Count() / (decimal)total * 100m, 0, MidpointRounding.AwayFromZero),
                    DepartmentId = group.Select(row => row.DepartmentId).FirstOrDefault(),
                    BranchId = group.Select(row => row.BranchId).FirstOrDefault(),
                })
                .OrderByDescending(row => row.Count)
                .ThenBy(row => row.Label, StringComparer.Ordinal),
        ];

    // ── attrition risk ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AttritionRiskDto>> AttritionAsync(
        AttritionQuery query,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        List<Person> people = await PeopleAsync(cancellationToken);
        IReadOnlyDictionary<Guid, string> departments = await DepartmentsAsync(cancellationToken);

        Dictionary<Guid, string> names = people.ToDictionary(
            person => person.Id, person => person.Name);

        IReadOnlyDictionary<Guid, int> ratings = await RatingsAsync(cancellationToken);
        IReadOnlyDictionary<Guid, Absence> absence = await AbsenceAsync(today, cancellationToken);

        List<AttritionRiskDto> board = [];

        foreach (Person person in people)
        {
            // Somebody who has left is not a retention risk, and somebody who
            // has not started cannot be scored on tenure.
            if (!InPost.Contains(person.Status) || person.Spell.Start is not DateOnly started)
            {
                continue;
            }

            if (query.DepartmentId is Guid departmentId && person.DepartmentId != departmentId)
            {
                continue;
            }

            Absence theirs = absence.GetValueOrDefault(person.Id, Absence.None);

            AttritionAssessment assessed = AttritionScoring.Assess(
                started,
                today,
                new EmployeeSignals
                {
                    OnLeave = person.Status == EmployeeStatus.OnLeave,
                    OnProbation = person.Status == EmployeeStatus.Probation,
                    FixedTerm = person.EmploymentType
                        is EmploymentType.Contract or EmploymentType.Temporary,

                    // Left off deliberately. Grade here is free text with no
                    // history behind it, so there is nothing to compare: the
                    // only thing available is whether a grade was ever
                    // recorded, and a blank field is not evidence somebody
                    // has stagnated. Scoring it would put eighteen points on
                    // every employee whose grade nobody filled in.
                    //
                    // This turns on once grade changes are dated — a grade
                    // history, or the level the frontend's model assumes.
                    NoGradeProgression = false,
                    LastPerformanceRating = ratings.TryGetValue(person.Id, out int rating)
                        ? rating
                        : null,
                    BradfordScore = theirs.Score,
                    AbsenceProtected = theirs.Protected,
                });

            if (query.AtLeast is AttritionRiskLevel least && assessed.Level < least)
            {
                continue;
            }

            board.Add(new AttritionRiskDto
            {
                EmployeeId = person.Id,
                EmployeeName = person.Name,
                JobTitle = person.JobTitle,
                DepartmentName = person.DepartmentId is Guid id
                    ? departments.GetValueOrDefault(id)
                    : null,
                ManagerName = person.ManagerId is Guid manager
                    ? names.GetValueOrDefault(manager)
                    : null,
                TenureYears = Rules.TenureYears(started, today),
                Score = assessed.Score,
                Level = assessed.Level,
                Factors = assessed.Factors,
                RecommendedAction = assessed.RecommendedAction,
                LastPerformanceRating = ratings.TryGetValue(person.Id, out int last)
                    ? last
                    : null,
                BradfordScore = theirs.Score,
            });
        }

        return
        [
            .. board
                .OrderByDescending(row => row.Score)
                .ThenBy(row => row.EmployeeName, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Clamp(query.Limit, 1, 500)),
        ];
    }

    // ── writing ─────────────────────────────────────────────────────────────

    public async Task<HeadcountPlanResult> SetTargetAsync(
        HeadcountPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!PlanPeriods.IsValid(request.Period))
        {
            return HeadcountPlanResult.Failed(HeadcountPlanOutcome.PeriodNotRecognised);
        }

        if (request.Target < 0)
        {
            return HeadcountPlanResult.Failed(HeadcountPlanOutcome.TargetNegative);
        }

        if (request.Budget is decimal budget && budget < 0m)
        {
            return HeadcountPlanResult.Failed(HeadcountPlanOutcome.BudgetNegative);
        }

        if (!await dbContext.Departments.AnyAsync(
            department => department.Id == request.DepartmentId, cancellationToken))
        {
            return HeadcountPlanResult.Failed(HeadcountPlanOutcome.DepartmentNotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        // An upsert, because two targets for one department and quarter is two
        // answers to one question and nothing downstream could choose between
        // them.
        HeadcountPlan? plan = await dbContext.HeadcountPlans.FirstOrDefaultAsync(
            row => row.DepartmentId == request.DepartmentId && row.Period == request.Period,
            cancellationToken);

        if (plan is null)
        {
            plan = new HeadcountPlan
            {
                Id = Guid.NewGuid(),
                TenantId = currentTenant.TenantId ?? Guid.Empty,
                DepartmentId = request.DepartmentId,
                Period = request.Period,
                CreatedByUserId = Guid.TryParse(requestContext.UserId, out Guid userId)
                    ? userId
                    : null,
                CreatedAt = now,
            };

            dbContext.HeadcountPlans.Add(plan);
        }

        plan.Target = request.Target;
        plan.Budget = request.Budget;
        plan.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        HeadcountPlanBoardDto board = await BoardAsync(request.Period, cancellationToken);

        return HeadcountPlanResult.Ok(
            board.Rows.First(row => row.DepartmentId == request.DepartmentId));
    }

    public async Task<HeadcountPlanOutcome> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        HeadcountPlan? plan = await dbContext.HeadcountPlans.FirstOrDefaultAsync(
            row => row.Id == id, cancellationToken);

        if (plan is null)
        {
            return HeadcountPlanOutcome.NotFound;
        }

        dbContext.HeadcountPlans.Remove(plan);

        await dbContext.SaveChangesAsync(cancellationToken);

        return HeadcountPlanOutcome.Succeeded;
    }

    // ── shared lookups ──────────────────────────────────────────────────────

    private async Task<Dictionary<Guid, HeadcountPlan>> PlansForAsync(
        string period,
        CancellationToken cancellationToken) =>
        await dbContext.HeadcountPlans
            .AsNoTracking()
            .Where(plan => plan.Period == period)
            .ToDictionaryAsync(plan => plan.DepartmentId, cancellationToken);

    private static IReadOnlyCollection<Spell> SpellsIn(
        IReadOnlyList<Person> people,
        Guid departmentId) =>
        [.. people.Where(person => person.DepartmentId == departmentId).Select(person => person.Spell)];

    // Where each department's hiring has got to, and how much of its gap is
    // already in hand.
    private async Task<IReadOnlyDictionary<Guid, Pipeline>> PipelinesAsync(
        DateOnly today,
        CancellationToken cancellationToken)
    {
        List<Vacancy> vacancies = await dbContext.JobRequisitions
            .AsNoTracking()
            .Where(requisition => requisition.DepartmentId != null
                && requisition.Status != RequisitionStatus.Closed
                && requisition.Status != RequisitionStatus.Cancelled)
            .Select(requisition => new Vacancy(
                requisition.Id,
                requisition.DepartmentId!.Value,
                requisition.Status,
                requisition.Openings,
                requisition.TargetStartDate))
            .ToListAsync(cancellationToken);

        if (vacancies.Count == 0)
        {
            return new Dictionary<Guid, Pipeline>();
        }

        List<Guid> ids = [.. vacancies.Select(vacancy => vacancy.Id)];

        // Hires who have accepted and not yet started: the part of a gap that
        // is solved and simply has not arrived.
        List<Guid> accepted = await dbContext.Candidates
            .AsNoTracking()
            .Where(candidate => ids.Contains(candidate.RequisitionId)
                && candidate.Stage == CandidateStage.Hired)
            .Select(candidate => candidate.RequisitionId)
            .ToListAsync(cancellationToken);

        Dictionary<Guid, int> hiredPer = accepted
            .GroupBy(id => id)
            .ToDictionary(group => group.Key, group => group.Count());

        return vacancies
            .GroupBy(vacancy => vacancy.DepartmentId)
            .ToDictionary(
                group => group.Key,
                group => new Pipeline
                {
                    ActiveRecruitments = group.Count(
                        vacancy => Requisitions.IsRecruiting(vacancy.Status)),

                    // Openings still to fill, not requisitions. A single
                    // requisition for three engineers is three vacancies.
                    OpenVacancies = group
                        .Where(vacancy => Requisitions.IsRecruiting(vacancy.Status))
                        .Sum(vacancy => Math.Max(
                            0,
                            Math.Max(1, vacancy.Openings)
                                - hiredPer.GetValueOrDefault(vacancy.Id))),

                    AcceptedNotStarted = group.Sum(
                        vacancy => hiredPer.GetValueOrDefault(vacancy.Id)),

                    // The furthest along decides: one role at offer and one
                    // not started is a department at offer stage.
                    Stage = group.Max(vacancy => StageOf(
                        vacancy.Status, hiredPer.GetValueOrDefault(vacancy.Id) > 0)),

                    ExpectedStartDate = group
                        .Where(vacancy => vacancy.TargetStartDate is not null)
                        .Select(vacancy => vacancy.TargetStartDate)
                        .DefaultIfEmpty(null)
                        .Min(),
                });
    }

    private static RecruitmentPipelineStage StageOf(RequisitionStatus status, bool anyHired) =>
        anyHired
            ? RecruitmentPipelineStage.OfferAccepted
            : status switch
            {
                RequisitionStatus.Draft or RequisitionStatus.PendingApproval or
                    RequisitionStatus.OnHold => RecruitmentPipelineStage.RequestRaised,
                RequisitionStatus.Approved => RecruitmentPipelineStage.RequisitionApproved,
                RequisitionStatus.Open => RecruitmentPipelineStage.VacancyPublished,
                RequisitionStatus.Interviewing => RecruitmentPipelineStage.Interviewing,
                RequisitionStatus.OfferStage => RecruitmentPipelineStage.OfferMade,
                RequisitionStatus.Filled => RecruitmentPipelineStage.OfferAccepted,
                _ => RecruitmentPipelineStage.NotStarted,
            };

    // The last rating anybody gave, from a completed review. Calibration wins
    // over the manager's own figure where HR has adjusted it, because that is
    // the rating of record.
    private async Task<IReadOnlyDictionary<Guid, int>> RatingsAsync(
        CancellationToken cancellationToken)
    {
        List<Rating> ratings = await dbContext.PerformanceReviews
            .AsNoTracking()
            .Where(review => review.Status == ReviewStatus.Completed
                && (review.CalibratedRating != null || review.ManagerRating != null))
            .Select(review => new Rating(
                review.EmployeeId,
                review.DueDate,
                review.CalibratedRating ?? review.ManagerRating!.Value))
            .ToListAsync(cancellationToken);

        return ratings
            .GroupBy(rating => rating.EmployeeId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(rating => rating.DueDate).First().Score);
    }

    // Each person's Bradford score, and whether any of the absence behind it
    // has been excluded. Protected absence drops the signal entirely rather
    // than reweighting it: scoring somebody's disability once as attendance
    // and again as flight risk is the same unlawful outcome twice.
    private async Task<IReadOnlyDictionary<Guid, Absence>> AbsenceAsync(
        DateOnly today,
        CancellationToken cancellationToken)
    {
        BradfordSettings settings = BradfordSettings.Default;
        DateOnly earliest = today.AddDays(-((settings.WindowWeeks * 7) + 400));

        List<SicknessRecord> records = await dbContext.SicknessRecords
            .AsNoTracking()
            .Where(record => record.StartDate >= earliest)
            .ToListAsync(cancellationToken);

        if (records.Count == 0)
        {
            return new Dictionary<Guid, Absence>();
        }

        HashSet<DateOnly> holidays =
        [
            .. await dbContext.PublicHolidays
                .AsNoTracking()
                .Select(holiday => holiday.Date)
                .ToListAsync(cancellationToken),
        ];

        // Mondays to Fridays. The sickness module resolves each person's own
        // pattern; here the score is only a risk signal, and loading every
        // schedule to weight one factor out of eight is not worth the query.
        IReadOnlySet<DayOfWeek> week = new HashSet<DayOfWeek>
        {
            DayOfWeek.Monday,
            DayOfWeek.Tuesday,
            DayOfWeek.Wednesday,
            DayOfWeek.Thursday,
            DayOfWeek.Friday,
        };

        Dictionary<Guid, Absence> absence = [];

        foreach (Guid employeeId in records.Select(record => record.EmployeeId).Distinct())
        {
            BradfordResult result = BradfordFactor.Calculate(
                employeeId, records, today, settings, week, holidays);

            absence[employeeId] = new Absence(
                result.Score,
                records.Exists(record =>
                    record.EmployeeId == employeeId && record.ExclusionCategory is not null));
        }

        return absence;
    }

    private async Task<List<Person>> PeopleAsync(CancellationToken cancellationToken)
    {
        _people ??= await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Status != EmployeeStatus.Deleted)
            .Select(employee => new Person(
                employee.Id,
                employee.FirstName + " " + employee.LastName,
                employee.JobTitle,
                employee.Status,
                employee.DepartmentId,
                employee.BranchId,
                employee.ManagerId,
                employee.EmploymentType,
                employee.Gender,
                employee.Grade,
                employee.WorkLocation,
                employee.DateOfBirth,
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
        string Name,
        string? JobTitle,
        EmployeeStatus Status,
        Guid? DepartmentId,
        Guid? BranchId,
        Guid? ManagerId,
        EmploymentType? EmploymentType,
        string? Gender,
        string? Grade,
        string? WorkLocation,
        DateOnly? DateOfBirth,
        Spell Spell);

    private sealed record Vacancy(
        Guid Id,
        Guid DepartmentId,
        RequisitionStatus Status,
        int Openings,
        DateOnly? TargetStartDate);

    private sealed record Rating(Guid EmployeeId, DateOnly DueDate, int Score);

    private readonly record struct Absence(decimal? Score, bool Protected)
    {
        public static readonly Absence None = new(null, false);
    }

    private sealed record Pipeline
    {
        public static readonly Pipeline Empty = new()
        {
            ActiveRecruitments = 0,
            OpenVacancies = 0,
            AcceptedNotStarted = 0,
            Stage = RecruitmentPipelineStage.NotStarted,
        };

        public required int ActiveRecruitments { get; init; }

        public required int OpenVacancies { get; init; }

        public required int AcceptedNotStarted { get; init; }

        public required RecruitmentPipelineStage Stage { get; init; }

        public DateOnly? ExpectedStartDate { get; init; }
    }
}
