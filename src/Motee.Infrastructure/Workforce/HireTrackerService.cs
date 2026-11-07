using Microsoft.EntityFrameworkCore;
using Motee.Application.Tenancy;
using Motee.Application.Workforce;
using Motee.Domain.Employees;
using Motee.Domain.Onboarding;
using Motee.Domain.Recruitment;
using Motee.Domain.Tenants;
using Motee.Domain.Workforce;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Workforce;

// Every hiring effort, resolved to one stage each.
//
// Joins five records and stores none. The frontend's version mints synthetic
// vacancies and candidates to fill holes in the chain so a timeline reads
// nicely; this does not — a hire that started halfway through reports the
// stage it is genuinely at, and the gap is the information rather than
// something to paper over.
internal sealed class HireTrackerService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : IHireTrackerService
{
    public async Task<IReadOnlyList<HireTrackerRowDto>> ListAsync(
        HireTrackerQuery query,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id, department => department.Name, cancellationToken);

        Dictionary<Guid, string> people = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Status != EmployeeStatus.Deleted)
            .ToDictionaryAsync(
                employee => employee.Id,
                employee => employee.FirstName + " " + employee.LastName,
                cancellationToken);

        List<Vacancy> vacancies = await dbContext.JobRequisitions
            .AsNoTracking()
            .Where(requisition => requisition.Status != RequisitionStatus.Cancelled)
            .Select(requisition => new Vacancy(
                requisition.Id,
                requisition.PositionTitle,
                requisition.DepartmentId,
                requisition.HiringManagerId,
                requisition.Status,
                requisition.Openings,
                requisition.PublishedAt,
                requisition.CreatedAt,
                requisition.UpdatedAt))
            .ToListAsync(cancellationToken);

        List<Guid> vacancyIds = [.. vacancies.Select(vacancy => vacancy.Id)];

        List<Applicant> applicants = vacancyIds.Count == 0
            ? []
            : await dbContext.Candidates
                .AsNoTracking()
                .Where(candidate => vacancyIds.Contains(candidate.RequisitionId))
                .Select(candidate => new Applicant(
                    candidate.RequisitionId,
                    candidate.FirstName + " " + candidate.LastName,
                    candidate.Stage,
                    candidate.Status,
                    candidate.CreatedEmployeeId,
                    candidate.UpdatedAt))
                .ToListAsync(cancellationToken);

        List<Guid> hiredEmployeeIds =
            [.. applicants.Where(a => a.CreatedEmployeeId is not null)
                .Select(a => a.CreatedEmployeeId!.Value)];

        Dictionary<Guid, Joiner> onboarding = hiredEmployeeIds.Count == 0
            ? []
            : await dbContext.OnboardingRecords
                .AsNoTracking()
                .Where(record => hiredEmployeeIds.Contains(record.EmployeeId))
                .Select(record => new Joiner(
                    record.EmployeeId, record.Stage, record.UpdatedAt))
                .ToDictionaryAsync(joiner => joiner.EmployeeId, cancellationToken);

        List<HireTrackerRowDto> rows = [];

        foreach (Vacancy vacancy in vacancies)
        {
            List<Applicant> theirs =
                [.. applicants.Where(candidate => candidate.RequisitionId == vacancy.Id)];

            List<Applicant> live =
                [.. theirs.Where(candidate => candidate.Status == CandidateStatus.Active)];

            List<Joiner> joiners =
            [
                .. theirs
                    .Where(candidate => candidate.CreatedEmployeeId is Guid id
                        && onboarding.ContainsKey(id))
                    .Select(candidate => onboarding[candidate.CreatedEmployeeId!.Value]),
            ];

            ChainFacts facts = new()
            {
                HasVacancy = true,
                VacancyPublished = vacancy.PublishedAt is not null,
                VacancyApproved = vacancy.Status != RequisitionStatus.Draft
                    && vacancy.Status != RequisitionStatus.PendingApproval,
                Applicants = live.Count(c => c.Stage == CandidateStage.Applicants),
                InSelection = live.Count(c =>
                    c.Stage is CandidateStage.Interview or CandidateStage.Interviewed),
                AtOffer = live.Count(c => c.Stage == CandidateStage.Offer),
                HiredNotInvited = live.Count(c =>
                    c.Stage == CandidateStage.Hired && c.CreatedEmployeeId is null),
                PreBoarding = joiners.Exists(j => j.Stage == OnboardingStage.PreBoarding),
                Onboarding = joiners.Exists(j => j.Stage != OnboardingStage.PreBoarding),
            };

            HiringStage stage = HiringChain.Resolve(facts);

            // The most recent movement anywhere on the effort. One candidate
            // progressing means the vacancy is not stalled, even if the
            // others have sat still.
            DateTimeOffset moved = new[] { vacancy.UpdatedAt }
                .Concat(theirs.Select(candidate => candidate.UpdatedAt))
                .Concat(joiners.Select(joiner => joiner.UpdatedAt))
                .Max();

            int days = Math.Max(
                0, today.DayNumber - DateOnly.FromDateTime(moved.UtcDateTime).DayNumber);

            rows.Add(new HireTrackerRowDto
            {
                Id = $"requisition:{vacancy.Id:D}",
                RequisitionId = vacancy.Id,
                Title = vacancy.Title,
                DepartmentId = vacancy.DepartmentId,
                DepartmentName = vacancy.DepartmentId is Guid id
                    ? departments.GetValueOrDefault(id)
                    : null,
                Stage = stage,
                Step = HiringChain.StepOf(stage),
                Detail = HiringChain.Detail(stage, facts),
                Person = NamedHire(theirs, joiners, people),
                HiringManagerId = vacancy.HiringManagerId,
                HiringManagerName = vacancy.HiringManagerId is Guid manager
                    ? people.GetValueOrDefault(manager)
                    : null,
                DaysSinceMoved = days,
                Stalled = HiringChain.IsStalled(days),
                Applicants = facts.Applicants,
                InSelection = facts.InSelection,
                Openings = Math.Max(1, vacancy.Openings),
            });
        }

        // Requests with no vacancy yet. Still a hiring effort, and the one
        // most likely to be forgotten — nothing downstream knows it exists.
        List<Request> requests = await dbContext.WorkforceRequests
            .AsNoTracking()
            .Where(request => request.RequisitionId == null && request.CancelledAt == null)
            .Select(request => new Request(
                request.Id,
                request.Position,
                request.DepartmentId,
                request.RequestedByEmployeeId,
                request.NumberOfHires,
                request.UpdatedAt))
            .ToListAsync(cancellationToken);

        foreach (Request request in requests)
        {
            ChainFacts facts = new() { HasVacancy = false };

            int days = Math.Max(
                0,
                today.DayNumber
                    - DateOnly.FromDateTime(request.UpdatedAt.UtcDateTime).DayNumber);

            rows.Add(new HireTrackerRowDto
            {
                Id = $"request:{request.Id:D}",
                WorkforceRequestId = request.Id,
                Title = request.Position,
                DepartmentId = request.DepartmentId,
                DepartmentName = request.DepartmentId is Guid id
                    ? departments.GetValueOrDefault(id)
                    : null,
                Stage = HiringStage.WorkforcePlanning,
                Step = HiringChain.StepOf(HiringStage.WorkforcePlanning),
                Detail = HiringChain.Detail(HiringStage.WorkforcePlanning, facts),
                HiringManagerId = request.RequestedByEmployeeId,
                HiringManagerName = request.RequestedByEmployeeId is Guid requester
                    ? people.GetValueOrDefault(requester)
                    : null,
                DaysSinceMoved = days,
                Stalled = HiringChain.IsStalled(days),
                Applicants = 0,
                InSelection = 0,
                Openings = Math.Max(1, request.NumberOfHires),
            });
        }

        IEnumerable<HireTrackerRowDto> filtered = rows;

        if (query.DepartmentId is Guid departmentId)
        {
            filtered = filtered.Where(row => row.DepartmentId == departmentId);
        }

        if (query.Stage is HiringStage wanted)
        {
            filtered = filtered.Where(row => row.Stage == wanted);
        }

        if (query.Stalled is bool stalled)
        {
            filtered = filtered.Where(row => row.Stalled == stalled);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string needle = query.Search.Trim();

            filtered = filtered.Where(row =>
                row.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (row.Person?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // Stalled first, then furthest from done: the order somebody scanning
        // for where to intervene actually wants.
        return
        [
            .. filtered
                .OrderByDescending(row => row.Stalled)
                .ThenByDescending(row => row.DaysSinceMoved)
                .ThenBy(row => row.Step)
                .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase),
        ];
    }

    // Whoever the effort is about, once somebody is identified. The joiner
    // first, then a hire, then an offer — the same precedence the stage uses.
    private static string? NamedHire(
        IReadOnlyList<Applicant> candidates,
        IReadOnlyList<Joiner> joiners,
        IReadOnlyDictionary<Guid, string> people)
    {
        if (joiners.Count > 0 && people.TryGetValue(joiners[0].EmployeeId, out string? joiner))
        {
            return joiner;
        }

        return candidates
            .Where(candidate => candidate.Status == CandidateStatus.Active)
            .OrderByDescending(candidate => candidate.Stage)
            .FirstOrDefault(candidate =>
                candidate.Stage is CandidateStage.Hired or CandidateStage.Offer)
            ?.Name;
    }

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken)
    {
        string? zone = null;

        if (currentTenant.TenantId is Guid tenantId)
        {
            TenantSettings? settings = await dbContext.Tenants
                .AsNoTracking()
                .Where(tenant => tenant.Id == tenantId)
                .Select(tenant => tenant.Settings)
                .FirstOrDefaultAsync(cancellationToken);

            zone = settings?.TimeZoneId;
        }

        return TenantClock.DayOf(timeProvider.GetUtcNow(), zone);
    }

    private sealed record Vacancy(
        Guid Id,
        string Title,
        Guid? DepartmentId,
        Guid? HiringManagerId,
        RequisitionStatus Status,
        int Openings,
        DateTimeOffset? PublishedAt,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record Applicant(
        Guid RequisitionId,
        string Name,
        CandidateStage Stage,
        CandidateStatus Status,
        Guid? CreatedEmployeeId,
        DateTimeOffset UpdatedAt);

    private sealed record Joiner(Guid EmployeeId, OnboardingStage Stage, DateTimeOffset UpdatedAt);

    private sealed record Request(
        Guid Id,
        string Position,
        Guid? DepartmentId,
        Guid? RequestedByEmployeeId,
        int NumberOfHires,
        DateTimeOffset UpdatedAt);
}
