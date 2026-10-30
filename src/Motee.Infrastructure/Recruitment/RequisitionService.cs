using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Recruitment;
using Motee.Application.Tenancy;
using Motee.Domain.Recruitment;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Recruitment;

// Vacancies.
//
// The status is read, not just stored. Every description recomputes the
// pipeline-driven half from the candidates, so the badge on the vacancy and the
// board beneath it cannot disagree — the alternative is a field somebody has to
// remember to set, which in practice means every vacancy sits on "open" forever
// and filling the last opening never closes the role.
internal sealed class RequisitionService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IRequisitionService
{
    public async Task<PagedResult<RequisitionDto>> ListAsync(
        RequisitionQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<JobRequisition> source = dbContext.JobRequisitions.AsNoTracking();

        if (query.Status is RequisitionStatus status)
        {
            source = source.Where(requisition => requisition.Status == status);
        }

        if (query.DepartmentId is Guid departmentId)
        {
            source = source.Where(requisition => requisition.DepartmentId == departmentId);
        }

        if (query.HiringManagerId is Guid managerId)
        {
            source = source.Where(requisition => requisition.HiringManagerId == managerId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string term = query.Search.Trim();

            source = source.Where(requisition =>
                EF.Functions.ILike(requisition.PositionTitle, $"%{term}%")
                    || (requisition.Reference != null
                        && EF.Functions.ILike(requisition.Reference, $"%{term}%")));
        }

        int total = await source.CountAsync(cancellationToken);

        List<JobRequisition> page = await source
            .OrderByDescending(requisition => requisition.CreatedAt)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        IReadOnlyList<RequisitionDto> described = await DescribeAsync(page, cancellationToken);

        // Applied after describing, because "still recruiting" depends on the
        // derived status and on today's date — neither of which the query
        // translator can see. The total is left alone rather than pretending it
        // narrowed: the alternative is loading every row to count them.
        if (query.RecruitingOnly)
        {
            described = [.. described.Where(requisition => requisition.IsAcceptingApplications)];
        }

        return new PagedResult<RequisitionDto>
        {
            Items = described,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<RequisitionDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        JobRequisition? requisition = await dbContext.JobRequisitions
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return requisition is null
            ? null
            : (await DescribeAsync([requisition], cancellationToken))[0];
    }

    public async Task<RequisitionResult> CreateAsync(
        RequisitionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Validate(request) is RequisitionOutcome problem)
        {
            return RequisitionResult.Failed(problem);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        JobRequisition requisition = new()
        {
            Id = Guid.NewGuid(),
            PositionTitle = request.PositionTitle.Trim(),
            DepartmentId = request.DepartmentId,
            HiringManagerId = request.HiringManagerId,
            RecruiterId = request.RecruiterId,
            HrBusinessPartnerId = request.HrBusinessPartnerId,
            InterviewPanel = [.. request.InterviewPanel ?? []],
            EmploymentType = request.EmploymentType,
            HiringPriority = request.HiringPriority,
            Location = request.Location?.Trim(),
            Openings = request.Openings,
            SalaryMin = request.SalaryMin,
            SalaryMax = request.SalaryMax,
            Currency = request.Currency?.Trim().ToUpperInvariant(),
            JobDescription = request.JobDescription?.Trim(),
            Qualifications = request.Qualifications?.Trim(),
            RequiredSkills = [.. request.RequiredSkills ?? []],
            TargetStartDate = request.TargetStartDate,
            ExpiryDate = request.ExpiryDate,

            // Entry and exit put back if the caller left them out, so a role
            // cannot be configured with no way in or out of its own pipeline.
            Stages = [.. RecruitmentPipeline.StagesFor(
                request.Stages ?? RecruitmentPipeline.Order)],

            // Draft, always. Writing a vacancy and publishing it are separate
            // acts, and creating straight into "open" would mean no way to save
            // a half-written one.
            Status = RequisitionStatus.Draft,
            CreatedByUserId = UserId(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.JobRequisitions.Add(requisition);
        await dbContext.SaveChangesAsync(cancellationToken);

        return RequisitionResult.Ok((await DescribeAsync([requisition], cancellationToken))[0]);
    }

    public async Task<RequisitionResult> UpdateAsync(
        Guid id,
        RequisitionRequest request,
        CancellationToken cancellationToken = default)
    {
        JobRequisition? requisition = await FindAsync(id, cancellationToken);

        if (requisition is null)
        {
            return RequisitionResult.Failed(RequisitionOutcome.NotFound);
        }

        if (Validate(request) is RequisitionOutcome problem)
        {
            return RequisitionResult.Failed(problem);
        }

        requisition.PositionTitle = request.PositionTitle.Trim();
        requisition.DepartmentId = request.DepartmentId;
        requisition.HiringManagerId = request.HiringManagerId;
        requisition.RecruiterId = request.RecruiterId;
        requisition.HrBusinessPartnerId = request.HrBusinessPartnerId;
        requisition.InterviewPanel = [.. request.InterviewPanel ?? []];
        requisition.EmploymentType = request.EmploymentType;
        requisition.HiringPriority = request.HiringPriority;
        requisition.Location = request.Location?.Trim();
        requisition.Openings = request.Openings;
        requisition.SalaryMin = request.SalaryMin;
        requisition.SalaryMax = request.SalaryMax;
        requisition.Currency = request.Currency?.Trim().ToUpperInvariant();
        requisition.JobDescription = request.JobDescription?.Trim();
        requisition.Qualifications = request.Qualifications?.Trim();
        requisition.RequiredSkills = [.. request.RequiredSkills ?? []];
        requisition.TargetStartDate = request.TargetStartDate;
        requisition.ExpiryDate = request.ExpiryDate;
        requisition.Stages = [.. RecruitmentPipeline.StagesFor(
            request.Stages ?? requisition.Stages)];
        requisition.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return RequisitionResult.Ok((await DescribeAsync([requisition], cancellationToken))[0]);
    }

    public async Task<IReadOnlyList<PublishWarningDto>> WarningsAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        JobRequisition? requisition = await dbContext.JobRequisitions
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return requisition is null ? [] : Warnings(requisition, await TodayAsync(cancellationToken));
    }

    public async Task<RequisitionResult> PublishAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        JobRequisition? requisition = await FindAsync(id, cancellationToken);

        if (requisition is null)
        {
            return RequisitionResult.Failed(RequisitionOutcome.NotFound);
        }

        if (requisition.Status is not (RequisitionStatus.Draft or RequisitionStatus.Approved))
        {
            return RequisitionResult.Failed(RequisitionOutcome.NotInThatState);
        }

        DateOnly today = await TodayAsync(cancellationToken);

        // The blocking warnings, as refusals. The advisory ones are left for
        // the screen to show — a vacancy with no salary band attracts the wrong
        // applicants, which is a judgement call rather than a fault.
        if (string.IsNullOrWhiteSpace(requisition.JobDescription))
        {
            return RequisitionResult.Failed(RequisitionOutcome.JobDescriptionRequired);
        }

        if (requisition.HiringManagerId is null)
        {
            return RequisitionResult.Failed(RequisitionOutcome.HiringManagerRequired);
        }

        if (Requisitions.HasExpired(requisition, today))
        {
            return RequisitionResult.Failed(RequisitionOutcome.ExpiryBeforeToday);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        // Issued once. A vacancy held and resumed keeps the number agencies
        // have already been given.
        requisition.Reference ??= await NextReferenceAsync(today.Year, cancellationToken);

        requisition.Status = RequisitionStatus.Open;
        requisition.PublishedAt ??= now;
        requisition.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return RequisitionResult.Ok((await DescribeAsync([requisition], cancellationToken))[0]);
    }

    public Task<RequisitionResult> HoldAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            id,
            RequisitionStatus.OnHold,
            status => Requisitions.IsRecruiting(status),
            cancellationToken);

    public Task<RequisitionResult> ResumeAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            id,
            RequisitionStatus.Open,
            status => status == RequisitionStatus.OnHold,
            cancellationToken);

    public Task<RequisitionResult> CloseAsync(
        Guid id,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            id,
            RequisitionStatus.Closed,
            status => status is not (RequisitionStatus.Closed or RequisitionStatus.Cancelled),
            cancellationToken);

    public Task<RequisitionResult> CancelAsync(
        Guid id,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            id,
            RequisitionStatus.Cancelled,
            status => status is not (RequisitionStatus.Closed or RequisitionStatus.Cancelled),
            cancellationToken);

    public async Task<RequisitionOutcome> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        JobRequisition? requisition = await FindAsync(id, cancellationToken);

        if (requisition is null)
        {
            return RequisitionOutcome.NotFound;
        }

        // Closing keeps the funnel; deleting would take the applicants, their
        // scorecards and their offers with it. Somebody who applied is a record
        // of what happened, not a draft.
        if (await dbContext.Candidates.AnyAsync(
                candidate => candidate.RequisitionId == id, cancellationToken))
        {
            return RequisitionOutcome.HasCandidates;
        }

        dbContext.JobRequisitions.Remove(requisition);
        await dbContext.SaveChangesAsync(cancellationToken);

        return RequisitionOutcome.Succeeded;
    }

    // ── internals ───────────────────────────────────────────────────────────

    private async Task<RequisitionResult> TransitionAsync(
        Guid id,
        RequisitionStatus to,
        Func<RequisitionStatus, bool> allowedFrom,
        CancellationToken cancellationToken)
    {
        JobRequisition? requisition = await FindAsync(id, cancellationToken);

        if (requisition is null)
        {
            return RequisitionResult.Failed(RequisitionOutcome.NotFound);
        }

        // Tested against the derived status, not the stored one. A vacancy
        // sitting on Open in the column but interviewing in practice is still a
        // live vacancy, and holding it must not be refused because the column
        // has not caught up.
        RequisitionStatus current = await DeriveAsync(requisition, cancellationToken);

        if (!allowedFrom(current))
        {
            return RequisitionResult.Failed(RequisitionOutcome.NotInThatState);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        requisition.Status = to;
        requisition.UpdatedAt = now;

        if (to is RequisitionStatus.Closed or RequisitionStatus.Cancelled)
        {
            requisition.ClosedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return RequisitionResult.Ok((await DescribeAsync([requisition], cancellationToken))[0]);
    }

    private Task<JobRequisition?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.JobRequisitions.FirstOrDefaultAsync(
            requisition => requisition.Id == id, cancellationToken);

    private async Task<RequisitionStatus> DeriveAsync(
        JobRequisition requisition,
        CancellationToken cancellationToken)
    {
        List<Candidate> candidates = await dbContext.Candidates
            .AsNoTracking()
            .Where(candidate => candidate.RequisitionId == requisition.Id)
            .ToListAsync(cancellationToken);

        return Requisitions.Derive(requisition.Status, requisition.Openings, candidates);
    }

    private static RequisitionOutcome? Validate(RequisitionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PositionTitle))
        {
            return RequisitionOutcome.TitleRequired;
        }

        if (request.Openings < 1)
        {
            return RequisitionOutcome.OpeningsNotPositive;
        }

        // A band whose floor is above its ceiling is a typo that would be
        // published to a job board.
        if (request.SalaryMin is decimal min
            && request.SalaryMax is decimal max
            && min > max)
        {
            return RequisitionOutcome.SalaryRangeInverted;
        }

        return null;
    }

    // What ought to be sorted out before this goes live.
    //
    // Blocking where the vacancy would waste an applicant's time — nothing to
    // read, nobody answerable. Advisory where it merely attracts the wrong
    // people. Returned as a list rather than one at a time, so the screen shows
    // the whole job instead of revealing it a refusal at a time.
    private static IReadOnlyList<PublishWarningDto> Warnings(
        JobRequisition requisition,
        DateOnly today)
    {
        List<PublishWarningDto> warnings = [];

        if (string.IsNullOrWhiteSpace(requisition.JobDescription))
        {
            warnings.Add(new PublishWarningDto
            {
                Field = "Job description",
                Message = "Applicants have nothing to read without one.",
                IsBlocking = true,
            });
        }

        if (requisition.HiringManagerId is null)
        {
            warnings.Add(new PublishWarningDto
            {
                Field = "Hiring manager",
                Message = "Nobody is assigned to own this hire.",
                IsBlocking = true,
            });
        }

        if (Requisitions.HasExpired(requisition, today))
        {
            warnings.Add(new PublishWarningDto
            {
                Field = "Closing date",
                Message = "The closing date has already passed.",
                IsBlocking = true,
            });
        }

        if (requisition.SalaryMin is null && requisition.SalaryMax is null)
        {
            warnings.Add(new PublishWarningDto
            {
                Field = "Salary",
                Message = "No salary range set — expect more unsuitable applicants.",
                IsBlocking = false,
            });
        }

        if (requisition.RequiredSkills.Count == 0)
        {
            warnings.Add(new PublishWarningDto
            {
                Field = "Required skills",
                Message = "Nothing listed, so applicants cannot tell whether to apply.",
                IsBlocking = false,
            });
        }

        if (requisition.RecruiterId is null)
        {
            warnings.Add(new PublishWarningDto
            {
                Field = "Recruiter",
                Message = "Applicants will have no owner until one is named.",
                IsBlocking = false,
            });
        }

        return warnings;
    }

    // Per year, continuing from the highest already issued. Read rather than
    // counted, so holding and resuming never hands out a reference twice.
    private async Task<string> NextReferenceAsync(int year, CancellationToken cancellationToken)
    {
        string prefix = $"{RequisitionReferences.Prefix}-{year}-";

        List<string> issued = await dbContext.JobRequisitions
            .AsNoTracking()
            .Where(requisition => requisition.Reference != null
                && requisition.Reference.StartsWith(prefix))
            .Select(requisition => requisition.Reference!)
            .ToListAsync(cancellationToken);

        int highest = issued.Aggregate(
            0,
            (max, reference) =>
                RequisitionReferences.TrySequenceOf(reference, year, out int sequence)
                    && sequence > max
                    ? sequence
                    : max);

        return RequisitionReferences.Format(year, highest + 1);
    }

    private async Task<IReadOnlyList<RequisitionDto>> DescribeAsync(
        IReadOnlyList<JobRequisition> requisitions,
        CancellationToken cancellationToken)
    {
        if (requisitions.Count == 0)
        {
            return [];
        }

        DateOnly today = await TodayAsync(cancellationToken);
        List<Guid> ids = [.. requisitions.Select(requisition => requisition.Id)];

        List<Candidate> candidates = await dbContext.Candidates
            .AsNoTracking()
            .Where(candidate => ids.Contains(candidate.RequisitionId))
            .ToListAsync(cancellationToken);

        List<Guid> candidateIds = [.. candidates.Select(candidate => candidate.Id)];

        List<CandidateOffer> offers = candidateIds.Count == 0
            ? []
            : await dbContext.CandidateOffers
                .AsNoTracking()
                .Where(offer => candidateIds.Contains(offer.CandidateId))
                .ToListAsync(cancellationToken);

        Dictionary<Guid, string> people = await PeopleAsync(requisitions, cancellationToken);

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id, department => department.Name, cancellationToken);

        return
        [
            .. requisitions.Select(requisition =>
            {
                List<Candidate> mine =
                [
                    .. candidates.Where(
                        candidate => candidate.RequisitionId == requisition.Id),
                ];

                HashSet<Guid> mineIds = [.. mine.Select(candidate => candidate.Id)];

                List<CandidateOffer> theirOffers =
                [
                    .. offers.Where(offer => mineIds.Contains(offer.CandidateId)),
                ];

                RequisitionStatus status = Requisitions.Derive(
                    requisition.Status, requisition.Openings, mine);

                int hired = mine.Count(
                    candidate => candidate.Stage == CandidateStage.Hired);

                return new RequisitionDto
                {
                    Id = requisition.Id,
                    Reference = requisition.Reference,
                    PositionTitle = requisition.PositionTitle,
                    DepartmentId = requisition.DepartmentId,
                    Department = Named(departments, requisition.DepartmentId),
                    HiringManagerId = requisition.HiringManagerId,
                    HiringManager = Named(people, requisition.HiringManagerId),
                    RecruiterId = requisition.RecruiterId,
                    Recruiter = Named(people, requisition.RecruiterId),
                    HrBusinessPartnerId = requisition.HrBusinessPartnerId,
                    HrBusinessPartner = Named(people, requisition.HrBusinessPartnerId),
                    InterviewPanel = [.. requisition.InterviewPanel],
                    InterviewPanelNames =
                    [
                        .. requisition.InterviewPanel
                            .Select(member => people.GetValueOrDefault(member))
                            .OfType<string>(),
                    ],
                    EmploymentType = requisition.EmploymentType,
                    Status = status,
                    HiringPriority = requisition.HiringPriority,
                    Location = requisition.Location,
                    Openings = requisition.Openings,
                    SalaryMin = requisition.SalaryMin,
                    SalaryMax = requisition.SalaryMax,
                    Currency = requisition.Currency,
                    JobDescription = requisition.JobDescription,
                    Qualifications = requisition.Qualifications,
                    RequiredSkills = [.. requisition.RequiredSkills],
                    TargetStartDate = requisition.TargetStartDate,
                    ExpiryDate = requisition.ExpiryDate,
                    HasExpired = Requisitions.HasExpired(requisition, today),

                    // Both halves matter: a held vacancy has not expired, and
                    // an expired one is not recruiting.
                    IsAcceptingApplications = Requisitions.IsRecruiting(status)
                        && !Requisitions.HasExpired(requisition, today),

                    Stages = [.. RecruitmentPipeline.StagesFor(requisition.Stages)],
                    Pipeline = Counts(requisition.Openings, hired, mine, theirOffers),
                    CreatedAt = requisition.CreatedAt,
                    PublishedAt = requisition.PublishedAt,
                    ClosedAt = requisition.ClosedAt,
                };
            }),
        ];
    }

    private static PipelineCountsDto Counts(
        int openings,
        int hired,
        IReadOnlyList<Candidate> candidates,
        IReadOnlyList<CandidateOffer> offers) => new()
        {
            Openings = openings,
            Hired = hired,
            Remaining = Math.Max(0, openings - hired),
            Active = candidates.Count(
                candidate => candidate.Status == CandidateStatus.Active),
            Rejected = candidates.Count(
                candidate => candidate.Status == CandidateStatus.Rejected),

            // Every stage listed, including the empty ones. A column missing
            // from a board is a column that renders as nothing at all.
            ByStage = RecruitmentPipeline.Order.ToDictionary(
                stage => stage,
                stage => candidates.Count(candidate => candidate.Stage == stage)),

            OffersSent = offers.Count,
            OffersAccepted = offers.Count(offer => offer.Status == OfferStatus.Accepted),
            OffersRejected = offers.Count(offer => offer.Status == OfferStatus.Rejected),
        };

    private static string? Named(IReadOnlyDictionary<Guid, string> names, Guid? id) =>
        id is Guid value ? names.GetValueOrDefault(value) : null;

    private async Task<Dictionary<Guid, string>> PeopleAsync(
        IReadOnlyList<JobRequisition> requisitions,
        CancellationToken cancellationToken)
    {
        List<Guid> ids =
        [
            .. requisitions
                .SelectMany(requisition => requisition.InterviewPanel
                    .Append(requisition.HiringManagerId ?? Guid.Empty)
                    .Append(requisition.RecruiterId ?? Guid.Empty)
                    .Append(requisition.HrBusinessPartnerId ?? Guid.Empty))
                .Where(id => id != Guid.Empty)
                .Distinct(),
        ];

        return ids.Count == 0
            ? []
            : await dbContext.Employees
                .AsNoTracking()
                .Where(employee => ids.Contains(employee.Id))
                .ToDictionaryAsync(
                    employee => employee.Id,
                    employee => employee.FirstName + " " + employee.LastName,
                    cancellationToken);
    }

    private Guid? UserId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken) =>
        TenantClock.DayOf(timeProvider.GetUtcNow(), await ZoneAsync(cancellationToken));

    // Scoped by id, because tenants are not themselves tenant-scoped, and the
    // whole Settings object because it is jsonb behind a value converter.
    private async Task<string?> ZoneAsync(CancellationToken cancellationToken)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return null;
        }

        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        return settings?.TimeZoneId;
    }
}
