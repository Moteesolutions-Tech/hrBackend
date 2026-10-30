using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Recruitment;
using Motee.Application.Tenancy;
using Motee.Domain.Recruitment;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Recruitment;

// Applicants and their progress.
//
// Moving somebody is the only operation here with real rules behind it, and
// they live in RecruitmentPipeline rather than in this file. That is so the
// board, the stage table and anything else that moves a candidate all ask the
// same question — a gate enforced inside one screen's handler is a gate
// drag-and-drop walks straight past.
internal sealed class CandidateService(
    MoteeDbContext dbContext,
    IEmployeeInvitationService invitations,
    ICurrentTenant currentTenant,
    IRequestContext requestContext,
    TimeProvider timeProvider) : ICandidateService
{
    private const int MinScore = 1;

    private const int MaxScore = 5;

    public async Task<PagedResult<CandidateDto>> ListAsync(
        CandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Candidate> source = dbContext.Candidates.AsNoTracking();

        if (query.RequisitionId is Guid requisitionId)
        {
            source = source.Where(candidate => candidate.RequisitionId == requisitionId);
        }

        if (query.Stage is CandidateStage stage)
        {
            source = source.Where(candidate => candidate.Stage == stage);
        }

        if (query.Status is CandidateStatus status)
        {
            source = source.Where(candidate => candidate.Status == status);
        }

        if (query.OwnerEmployeeId is Guid ownerId)
        {
            source = source.Where(candidate => candidate.OwnerEmployeeId == ownerId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string term = query.Search.Trim();

            source = source.Where(candidate =>
                EF.Functions.ILike(candidate.FirstName, $"%{term}%")
                    || EF.Functions.ILike(candidate.LastName, $"%{term}%")
                    || EF.Functions.ILike(candidate.Email, $"%{term}%"));
        }

        int total = await source.CountAsync(cancellationToken);

        List<Candidate> page = await source
            .OrderByDescending(candidate => candidate.AppliedAt)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<CandidateDto>
        {
            Items = await DescribeAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<CandidateDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Candidate? candidate = await dbContext.Candidates
            .AsNoTracking()
            .FirstOrDefaultAsync(found => found.Id == id, cancellationToken);

        return candidate is null ? null : (await DescribeAsync([candidate], cancellationToken))[0];
    }

    public async Task<CandidateResult> CreateAsync(
        CandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Validate(request) is CandidateOutcome problem)
        {
            return CandidateResult.Failed(problem);
        }

        JobRequisition? requisition = await dbContext.JobRequisitions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                found => found.Id == request.RequisitionId, cancellationToken);

        if (requisition is null)
        {
            return CandidateResult.Failed(CandidateOutcome.RequisitionNotFound);
        }

        DateOnly today = await TodayAsync(cancellationToken);

        // Checked against the derived status, not the column: a vacancy that
        // has quietly filled every opening is no longer taking applications,
        // and nothing writes that to the row.
        List<Candidate> existing = await dbContext.Candidates
            .Where(candidate => candidate.RequisitionId == requisition.Id)
            .ToListAsync(cancellationToken);

        RequisitionStatus status = Requisitions.Derive(
            requisition.Status, requisition.Openings, existing);

        if (!Requisitions.IsRecruiting(status) || Requisitions.HasExpired(requisition, today))
        {
            return CandidateResult.Failed(CandidateOutcome.NotAcceptingApplications);
        }

        string email = request.Email.Trim();

        // Their first application is the one carrying the history. The unique
        // index holds this too — two tabs are faster than a read followed by a
        // write — but refusing here gives a reason rather than a constraint
        // violation.
        if (existing.Any(candidate =>
                string.Equals(candidate.Email, email, StringComparison.OrdinalIgnoreCase)))
        {
            return CandidateResult.Failed(CandidateOutcome.AlreadyApplied);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        Candidate applicant = new()
        {
            Id = Guid.NewGuid(),
            RequisitionId = requisition.Id,
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            Email = email,
            Phone = request.Phone?.Trim(),
            Source = request.Source?.Trim(),
            Skills = [.. request.Skills ?? []],
            ExperienceSummary = request.ExperienceSummary?.Trim(),
            Notes = request.Notes?.Trim(),
            LinkedIn = request.LinkedIn?.Trim(),
            CvFileId = request.CvFileId,
            CoverLetterFileId = request.CoverLetterFileId,

            // Falls to the vacancy's recruiter rather than to nobody. An
            // unowned applicant is one nobody is answerable for, and "who is
            // chasing Chidi?" then has no answer.
            OwnerEmployeeId = request.OwnerEmployeeId ?? requisition.RecruiterId,

            Stage = CandidateStage.Applicants,
            Status = CandidateStatus.Active,
            AppliedAt = now,
            UpdatedAt = now,
        };

        dbContext.Candidates.Add(applicant);
        await dbContext.SaveChangesAsync(cancellationToken);

        return CandidateResult.Ok((await DescribeAsync([applicant], cancellationToken))[0]);
    }

    public async Task<CandidateResult> UpdateAsync(
        Guid id,
        CandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        Candidate? candidate = await FindAsync(id, cancellationToken);

        if (candidate is null)
        {
            return CandidateResult.Failed(CandidateOutcome.NotFound);
        }

        if (Validate(request) is CandidateOutcome problem)
        {
            return CandidateResult.Failed(problem);
        }

        candidate.FirstName = request.FirstName.Trim();
        candidate.LastName = request.LastName.Trim();
        candidate.Email = request.Email.Trim();
        candidate.Phone = request.Phone?.Trim();
        candidate.Source = request.Source?.Trim();
        candidate.Skills = [.. request.Skills ?? []];
        candidate.ExperienceSummary = request.ExperienceSummary?.Trim();
        candidate.Notes = request.Notes?.Trim();
        candidate.LinkedIn = request.LinkedIn?.Trim();
        candidate.CvFileId = request.CvFileId;
        candidate.CoverLetterFileId = request.CoverLetterFileId;

        if (request.OwnerEmployeeId is Guid owner)
        {
            candidate.OwnerEmployeeId = owner;
        }

        // Not touched. UpdatedAt means "when did they last move", because that
        // is what the board's "nine days in this stage" is read from —
        // correcting a typo in somebody's phone number must not make them look
        // freshly chased.
        await dbContext.SaveChangesAsync(cancellationToken);

        return CandidateResult.Ok((await DescribeAsync([candidate], cancellationToken))[0]);
    }

    public async Task<MoveRefusal> CanMoveAsync(
        Guid id,
        CandidateStage to,
        CancellationToken cancellationToken = default)
    {
        Candidate? candidate = await dbContext.Candidates
            .AsNoTracking()
            .FirstOrDefaultAsync(found => found.Id == id, cancellationToken);

        return candidate is null
            ? MoveRefusal.AlreadyThere
            : await RefusalAsync(candidate, to, cancellationToken);
    }

    public async Task<CandidateResult> MoveAsync(
        Guid id,
        CandidateStage to,
        CancellationToken cancellationToken = default)
    {
        Candidate? candidate = await FindAsync(id, cancellationToken);

        if (candidate is null)
        {
            return CandidateResult.Failed(CandidateOutcome.NotFound);
        }

        if (await RefusalAsync(candidate, to, cancellationToken) is var refusal
            && refusal != MoveRefusal.None)
        {
            return CandidateResult.Refused(refusal);
        }

        candidate.Stage = to;
        candidate.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return CandidateResult.Ok((await DescribeAsync([candidate], cancellationToken))[0]);
    }

    public async Task<CandidateResult> RejectAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default)
    {
        Candidate? candidate = await FindAsync(id, cancellationToken);

        if (candidate is null)
        {
            return CandidateResult.Failed(CandidateOutcome.NotFound);
        }

        // A rejection with no reason is one nobody can answer a question about
        // six months later, and "why did we turn them down" is a question that
        // gets asked.
        if (string.IsNullOrWhiteSpace(reason))
        {
            return CandidateResult.Failed(CandidateOutcome.ReasonRequired);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        // The stage is left exactly where it was. Rejected at offer and
        // rejected on sight are different facts about the same process, and
        // collapsing them loses the funnel.
        candidate.Status = CandidateStatus.Rejected;
        candidate.RejectionReason = reason.Trim();
        candidate.RejectedAt = now;
        candidate.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return CandidateResult.Ok((await DescribeAsync([candidate], cancellationToken))[0]);
    }

    public async Task<CandidateResult> RestoreAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Candidate? candidate = await FindAsync(id, cancellationToken);

        if (candidate is null)
        {
            return CandidateResult.Failed(CandidateOutcome.NotFound);
        }

        if (candidate.Status != CandidateStatus.Rejected)
        {
            return CandidateResult.Failed(CandidateOutcome.NotRejected);
        }

        candidate.Status = CandidateStatus.Active;
        candidate.RejectionReason = null;
        candidate.RejectedAt = null;
        candidate.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return CandidateResult.Ok((await DescribeAsync([candidate], cancellationToken))[0]);
    }

    public async Task<CandidateResult> AssignOwnerAsync(
        Guid id,
        Guid? ownerEmployeeId,
        CancellationToken cancellationToken = default)
    {
        Candidate? candidate = await FindAsync(id, cancellationToken);

        if (candidate is null)
        {
            return CandidateResult.Failed(CandidateOutcome.NotFound);
        }

        candidate.OwnerEmployeeId = ownerEmployeeId;

        await dbContext.SaveChangesAsync(cancellationToken);

        return CandidateResult.Ok((await DescribeAsync([candidate], cancellationToken))[0]);
    }

    // ── evidence ────────────────────────────────────────────────────────────

    public async Task<CandidateResult> ScoreAsync(
        Guid id,
        ScorecardRequest request,
        CancellationToken cancellationToken = default)
    {
        Candidate? candidate = await FindAsync(id, cancellationToken);

        if (candidate is null)
        {
            return CandidateResult.Failed(CandidateOutcome.NotFound);
        }

        if (request.Criteria.Count == 0)
        {
            return CandidateResult.Failed(CandidateOutcome.CriteriaRequired);
        }

        if (request.Criteria.Any(
                criterion => criterion.Score is < MinScore or > MaxScore))
        {
            return CandidateResult.Failed(CandidateOutcome.ScoreOutOfRange);
        }

        dbContext.Scorecards.Add(new Scorecard
        {
            Id = Guid.NewGuid(),
            CandidateId = candidate.Id,
            InterviewId = request.InterviewId,
            ByEmployeeId = await AuthorAsync(cancellationToken),

            // Kept beside the id so a scorecard still says who wrote it after
            // that person has left and their employee row has gone.
            ByName = await AuthorNameAsync(cancellationToken),

            Criteria = [.. request.Criteria],
            Recommendation = request.Recommendation,
            Comment = request.Comment?.Trim(),
            CreatedAt = timeProvider.GetUtcNow(),
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return CandidateResult.Ok((await DescribeAsync([candidate], cancellationToken))[0]);
    }

    public async Task<CandidateResult> SendOfferAsync(
        Guid id,
        OfferRequest request,
        CancellationToken cancellationToken = default)
    {
        Candidate? candidate = await FindAsync(id, cancellationToken);

        if (candidate is null)
        {
            return CandidateResult.Failed(CandidateOutcome.NotFound);
        }

        // Two live offers is two different numbers the candidate could be
        // holding, and no way to say which one they accepted.
        bool outstanding = await dbContext.CandidateOffers
            .AnyAsync(
                offer => offer.CandidateId == id && offer.Status == OfferStatus.Sent,
                cancellationToken);

        if (outstanding)
        {
            return CandidateResult.Failed(CandidateOutcome.OfferOutstanding);
        }

        dbContext.CandidateOffers.Add(new CandidateOffer
        {
            Id = Guid.NewGuid(),
            CandidateId = candidate.Id,
            Status = OfferStatus.Sent,
            Salary = request.Salary,
            Currency = request.Currency?.Trim().ToUpperInvariant(),
            StartDate = request.StartDate,
            Notes = request.Notes?.Trim(),
            AttachmentFileId = request.AttachmentFileId,
            SentAt = timeProvider.GetUtcNow(),
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return CandidateResult.Ok((await DescribeAsync([candidate], cancellationToken))[0]);
    }

    public async Task<CandidateResult> RespondToOfferAsync(
        Guid candidateId,
        Guid offerId,
        OfferResponseRequest request,
        CancellationToken cancellationToken = default)
    {
        Candidate? candidate = await FindAsync(candidateId, cancellationToken);

        if (candidate is null)
        {
            return CandidateResult.Failed(CandidateOutcome.NotFound);
        }

        CandidateOffer? offer = await dbContext.CandidateOffers.FirstOrDefaultAsync(
            found => found.Id == offerId && found.CandidateId == candidateId,
            cancellationToken);

        if (offer is null)
        {
            return CandidateResult.Failed(CandidateOutcome.OfferNotFound);
        }

        // Sent is where an offer starts, not something a candidate can reply.
        if (request.Status == OfferStatus.Sent)
        {
            return CandidateResult.Failed(CandidateOutcome.NotAnAnswer);
        }

        // Answering twice would overwrite the first answer and its timestamp,
        // which is the record of what the candidate actually said and when.
        if (!Offers.CanRespond(offer.Status))
        {
            return CandidateResult.Failed(CandidateOutcome.OfferAlreadyAnswered);
        }

        offer.Status = request.Status;
        offer.ResponseNote = request.ResponseNote?.Trim();
        offer.RespondedAt = timeProvider.GetUtcNow();
        offer.RespondedByUserId = UserId();

        await dbContext.SaveChangesAsync(cancellationToken);

        return CandidateResult.Ok((await DescribeAsync([candidate], cancellationToken))[0]);
    }

    // ── the handoff ─────────────────────────────────────────────────────────

    public async Task<CandidateResult> SendToOnboardingAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Candidate? candidate = await FindAsync(id, cancellationToken);

        if (candidate is null)
        {
            return CandidateResult.Failed(CandidateOutcome.NotFound);
        }

        // Returned rather than quietly done again. The stage table and the
        // candidate drawer both offer this, and a second employee record is a
        // real person duplicated on the payroll.
        if (candidate.CreatedEmployeeId is not null)
        {
            return CandidateResult.Failed(CandidateOutcome.AlreadyOnboarding);
        }

        // The pipeline's gates already guard the way into Hired, so this only
        // fires when a caller skips the board entirely — which is exactly the
        // case worth refusing.
        if (candidate.Stage != CandidateStage.Hired)
        {
            return CandidateResult.Failed(CandidateOutcome.NotHired);
        }

        JobRequisition? requisition = await dbContext.JobRequisitions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                found => found.Id == candidate.RequisitionId, cancellationToken);

        if (requisition is null)
        {
            return CandidateResult.Failed(CandidateOutcome.RequisitionNotFound);
        }

        if (requisition.DepartmentId is not Guid departmentId)
        {
            return CandidateResult.Failed(CandidateOutcome.DepartmentRequired);
        }

        // The accepted offer's date is what the candidate agreed to. The
        // vacancy's target date is only ever what HR hoped for, so it is the
        // fallback rather than the answer.
        DateOnly? startDate = await dbContext.CandidateOffers
            .AsNoTracking()
            .Where(offer => offer.CandidateId == candidate.Id
                && offer.Status == OfferStatus.Accepted)
            .OrderByDescending(offer => offer.SentAt)
            .Select(offer => offer.StartDate)
            .FirstOrDefaultAsync(cancellationToken) ?? requisition.TargetStartDate;

        // Creates the employee, issues the join token and ensures the
        // onboarding record, in that order and in one call. Nothing here
        // rebuilds any of that: a second path to creating an employee is a
        // second set of rules about what an employee is.
        InviteResult invited = await invitations.InviteAsync(
            new InviteRequest
            {
                FirstName = candidate.FirstName,
                LastName = candidate.LastName,
                Email = candidate.Email,
                JobTitle = requisition.PositionTitle,
                DepartmentId = departmentId,
                EmploymentType = requisition.EmploymentType,
                StartDate = startDate,
                ManagerId = requisition.HiringManagerId,
            },
            cancellationToken);

        if (!invited.Succeeded)
        {
            return CandidateResult.Failed(CandidateOutcome.CouldNotCreateEmployee);
        }

        candidate.CreatedEmployeeId = invited.Employee!.Id;
        candidate.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return CandidateResult.Ok((await DescribeAsync([candidate], cancellationToken))[0]);
    }

    // ── internals ───────────────────────────────────────────────────────────

    private Task<Candidate?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Candidates.FirstOrDefaultAsync(
            candidate => candidate.Id == id, cancellationToken);

    private async Task<MoveRefusal> RefusalAsync(
        Candidate candidate,
        CandidateStage to,
        CancellationToken cancellationToken)
    {
        JobRequisition? requisition = await dbContext.JobRequisitions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                found => found.Id == candidate.RequisitionId, cancellationToken);

        if (requisition is null)
        {
            return MoveRefusal.StageNotEnabled;
        }

        List<Scorecard> scorecards = await dbContext.Scorecards
            .AsNoTracking()
            .Where(scorecard => scorecard.CandidateId == candidate.Id)
            .ToListAsync(cancellationToken);

        List<CandidateOffer> offers = await dbContext.CandidateOffers
            .AsNoTracking()
            .Where(offer => offer.CandidateId == candidate.Id)
            .ToListAsync(cancellationToken);

        int hired = await dbContext.Candidates
            .AsNoTracking()
            .CountAsync(
                other => other.RequisitionId == candidate.RequisitionId
                    && other.Stage == CandidateStage.Hired,
                cancellationToken);

        return RecruitmentPipeline.Refusal(
            candidate,
            to,
            RecruitmentPipeline.StagesFor(requisition.Stages),
            scorecards,
            offers,
            requisition.Openings,
            hired);
    }

    private static CandidateOutcome? Validate(CandidateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FirstName)
            || string.IsNullOrWhiteSpace(request.LastName))
        {
            return CandidateOutcome.NameRequired;
        }

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return CandidateOutcome.EmailRequired;
        }

        // An applicant nobody can reply to is an application nobody can act on,
        // and a typo here is only discovered when the rejection bounces.
        return MailAddress.TryCreate(request.Email.Trim(), out _)
            ? null
            : CandidateOutcome.EmailInvalid;
    }

    private async Task<IReadOnlyList<CandidateDto>> DescribeAsync(
        IReadOnlyList<Candidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        List<Guid> ids = [.. candidates.Select(candidate => candidate.Id)];

        List<Scorecard> scorecards = await dbContext.Scorecards
            .AsNoTracking()
            .Where(scorecard => ids.Contains(scorecard.CandidateId))
            .ToListAsync(cancellationToken);

        List<CandidateOffer> offers = await dbContext.CandidateOffers
            .AsNoTracking()
            .Where(offer => ids.Contains(offer.CandidateId))
            .ToListAsync(cancellationToken);

        List<Guid> requisitionIds =
        [
            .. candidates.Select(candidate => candidate.RequisitionId).Distinct(),
        ];

        Dictionary<Guid, string> titles = await dbContext.JobRequisitions
            .AsNoTracking()
            .Where(requisition => requisitionIds.Contains(requisition.Id))
            .ToDictionaryAsync(
                requisition => requisition.Id,
                requisition => requisition.PositionTitle,
                cancellationToken);

        List<Guid> ownerIds =
        [
            .. candidates.Select(candidate => candidate.OwnerEmployeeId).OfType<Guid>().Distinct(),
        ];

        Dictionary<Guid, string> owners = ownerIds.Count == 0
            ? []
            : await dbContext.Employees
                .AsNoTracking()
                .Where(employee => ownerIds.Contains(employee.Id))
                .ToDictionaryAsync(
                    employee => employee.Id,
                    employee => employee.FirstName + " " + employee.LastName,
                    cancellationToken);

        return
        [
            .. candidates.Select(candidate =>
            {
                List<Scorecard> theirs =
                [
                    .. scorecards.Where(scorecard => scorecard.CandidateId == candidate.Id),
                ];

                return new CandidateDto
                {
                    Id = candidate.Id,
                    RequisitionId = candidate.RequisitionId,
                    RequisitionTitle = titles.GetValueOrDefault(candidate.RequisitionId),
                    Name = candidate.FullName,
                    Email = candidate.Email,
                    Phone = candidate.Phone,
                    Source = candidate.Source,
                    Stage = candidate.Stage,
                    Status = candidate.Status,
                    Skills = [.. candidate.Skills],
                    ExperienceSummary = candidate.ExperienceSummary,
                    Notes = candidate.Notes,
                    LinkedIn = candidate.LinkedIn,
                    CvFileId = candidate.CvFileId,
                    CoverLetterFileId = candidate.CoverLetterFileId,
                    OwnerEmployeeId = candidate.OwnerEmployeeId,
                    OwnerName = candidate.OwnerEmployeeId is Guid owner
                        ? owners.GetValueOrDefault(owner)
                        : null,
                    CreatedEmployeeId = candidate.CreatedEmployeeId,
                    RejectionReason = candidate.RejectionReason,
                    RejectedAt = candidate.RejectedAt,
                    AppliedAt = candidate.AppliedAt,
                    UpdatedAt = candidate.UpdatedAt,
                    DaysInStage = Candidates.DaysInStage(candidate, now),
                    Scorecards =
                    [
                        .. theirs
                            .OrderBy(scorecard => scorecard.CreatedAt)
                            .Select(scorecard => new ScorecardDto
                            {
                                Id = scorecard.Id,
                                InterviewId = scorecard.InterviewId,
                                ByEmployeeId = scorecard.ByEmployeeId,
                                ByName = scorecard.ByName,
                                Criteria = [.. scorecard.Criteria],
                                Overall = Scorecards.Overall(scorecard.Criteria),
                                Recommendation = scorecard.Recommendation,
                                Comment = scorecard.Comment,
                                CreatedAt = scorecard.CreatedAt,
                            }),
                    ],

                    // Null when nobody has met them, which is not zero.
                    Score = Scorecards.AverageFor(theirs),

                    Offers =
                    [
                        .. offers
                            .Where(offer => offer.CandidateId == candidate.Id)
                            .OrderBy(offer => offer.SentAt)
                            .Select(offer => new OfferDto
                            {
                                Id = offer.Id,
                                Status = offer.Status,
                                Salary = offer.Salary,
                                Currency = offer.Currency,
                                StartDate = offer.StartDate,
                                Notes = offer.Notes,
                                AttachmentFileId = offer.AttachmentFileId,
                                SentAt = offer.SentAt,
                                RespondedAt = offer.RespondedAt,
                                ResponseNote = offer.ResponseNote,
                            }),
                    ],
                };
            }),
        ];
    }

    private Guid? UserId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    // Users are not tenant-scoped, so this needs its own tenant clause.
    private async Task<Guid?> AuthorAsync(CancellationToken cancellationToken)
    {
        if (UserId() is not Guid userId || currentTenant.TenantId is not Guid tenantId)
        {
            return null;
        }

        return await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId && user.TenantId == tenantId)
            .Select(user => user.EmployeeId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<string?> AuthorNameAsync(CancellationToken cancellationToken)
    {
        if (UserId() is not Guid userId || currentTenant.TenantId is not Guid tenantId)
        {
            return null;
        }

        return await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId && user.TenantId == tenantId)
            .Select(user => (user.FirstName + " " + user.LastName).Trim())
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken) =>
        TenantClock.DayOf(timeProvider.GetUtcNow(), await ZoneAsync(cancellationToken));

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
