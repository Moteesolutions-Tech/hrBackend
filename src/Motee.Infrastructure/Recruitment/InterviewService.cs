using Microsoft.EntityFrameworkCore;
using Motee.Application.Recruitment;
using Motee.Domain.Recruitment;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Recruitment;

// Interviews.
//
// Clashes are reported, not refused. A panel member double-booked is usually a
// mistake, but sometimes it is a deliberate overlap somebody intends to sort
// out, and refusing outright leaves them unable to record the diary they
// already have. The warning rides on the response so the screen can show it.
internal sealed class InterviewService(
    MoteeDbContext dbContext,
    TimeProvider timeProvider) : IInterviewService
{
    public async Task<IReadOnlyList<InterviewDto>> ForCandidateAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default) =>
        await DescribeAsync(
            await dbContext.Interviews
                .AsNoTracking()
                .Where(interview => interview.CandidateId == candidateId)
                .OrderBy(interview => interview.ScheduledAt)
                .ToListAsync(cancellationToken),
            cancellationToken);

    public async Task<IReadOnlyList<InterviewDto>> UpcomingAsync(
        Guid? requisitionId = null,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        IQueryable<Interview> source = dbContext.Interviews
            .AsNoTracking()
            .Where(interview => interview.Status == InterviewStatus.Scheduled
                && interview.ScheduledAt >= now);

        if (requisitionId is Guid id)
        {
            source = source.Where(interview => interview.RequisitionId == id);
        }

        return await DescribeAsync(
            await source.OrderBy(interview => interview.ScheduledAt).ToListAsync(cancellationToken),
            cancellationToken);
    }

    public async Task<InterviewResult> ScheduleAsync(
        InterviewRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Validate(request) is InterviewOutcome problem)
        {
            return InterviewResult.Failed(problem);
        }

        Candidate? candidate = await dbContext.Candidates
            .AsNoTracking()
            .FirstOrDefaultAsync(found => found.Id == request.CandidateId, cancellationToken);

        if (candidate is null)
        {
            return InterviewResult.Failed(InterviewOutcome.CandidateNotFound);
        }

        if (candidate.Status == CandidateStatus.Rejected)
        {
            return InterviewResult.Failed(InterviewOutcome.CandidateRejected);
        }

        JobRequisition? requisition = await dbContext.JobRequisitions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                found => found.Id == candidate.RequisitionId, cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        Interview interview = new()
        {
            Id = Guid.NewGuid(),
            CandidateId = candidate.Id,
            RequisitionId = candidate.RequisitionId,
            Round = request.Round.Trim(),

            // Npgsql refuses a non-UTC offset on timestamptz, and a caller
            // sending their own local offset is the normal case.
            ScheduledAt = request.ScheduledAt.ToUniversalTime(),

            DurationMinutes = request.DurationMinutes,
            Mode = request.Mode,

            // The vacancy's standing panel rather than nobody, so an interview
            // is not booked with an empty room by default.
            Panel = [.. request.Panel ?? requisition?.InterviewPanel ?? []],

            Location = request.Location?.Trim(),
            Status = InterviewStatus.Scheduled,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.Interviews.Add(interview);
        await dbContext.SaveChangesAsync(cancellationToken);

        return InterviewResult.Ok((await DescribeAsync([interview], cancellationToken))[0]);
    }

    public async Task<InterviewResult> RescheduleAsync(
        Guid id,
        InterviewRequest request,
        CancellationToken cancellationToken = default)
    {
        Interview? interview = await FindAsync(id, cancellationToken);

        if (interview is null)
        {
            return InterviewResult.Failed(InterviewOutcome.NotFound);
        }

        if (Validate(request) is InterviewOutcome problem)
        {
            return InterviewResult.Failed(problem);
        }

        // A cancelled or completed interview is history. Moving it would
        // rewrite what happened rather than change what is going to.
        if (!Interviews.CanCancel(interview.Status))
        {
            return InterviewResult.Failed(InterviewOutcome.NotScheduled);
        }

        interview.Round = request.Round.Trim();
        interview.ScheduledAt = request.ScheduledAt.ToUniversalTime();
        interview.DurationMinutes = request.DurationMinutes;
        interview.Mode = request.Mode;
        interview.Location = request.Location?.Trim();
        interview.UpdatedAt = timeProvider.GetUtcNow();

        if (request.Panel is not null)
        {
            interview.Panel = [.. request.Panel];
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return InterviewResult.Ok((await DescribeAsync([interview], cancellationToken))[0]);
    }

    public Task<InterviewResult> CompleteAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(id, InterviewStatus.Completed, cancellationToken);

    public Task<InterviewResult> CancelAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(id, InterviewStatus.Cancelled, cancellationToken);

    // ── internals ───────────────────────────────────────────────────────────

    private async Task<InterviewResult> TransitionAsync(
        Guid id,
        InterviewStatus to,
        CancellationToken cancellationToken)
    {
        Interview? interview = await FindAsync(id, cancellationToken);

        if (interview is null)
        {
            return InterviewResult.Failed(InterviewOutcome.NotFound);
        }

        if (!Interviews.CanCancel(interview.Status))
        {
            return InterviewResult.Failed(InterviewOutcome.NotScheduled);
        }

        interview.Status = to;
        interview.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return InterviewResult.Ok((await DescribeAsync([interview], cancellationToken))[0]);
    }

    private Task<Interview?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Interviews.FirstOrDefaultAsync(
            interview => interview.Id == id, cancellationToken);

    private static InterviewOutcome? Validate(InterviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Round))
        {
            return InterviewOutcome.RoundRequired;
        }

        return request.DurationMinutes < 1 ? InterviewOutcome.DurationNotPositive : null;
    }

    private async Task<IReadOnlyList<InterviewDto>> DescribeAsync(
        IReadOnlyList<Interview> interviews,
        CancellationToken cancellationToken)
    {
        if (interviews.Count == 0)
        {
            return [];
        }

        List<Guid> candidateIds =
        [
            .. interviews.Select(interview => interview.CandidateId).Distinct(),
        ];

        Dictionary<Guid, string> candidates = await dbContext.Candidates
            .AsNoTracking()
            .Where(candidate => candidateIds.Contains(candidate.Id))
            .ToDictionaryAsync(
                candidate => candidate.Id,
                candidate => candidate.FirstName + " " + candidate.LastName,
                cancellationToken);

        List<Guid> requisitionIds =
        [
            .. interviews.Select(interview => interview.RequisitionId).Distinct(),
        ];

        Dictionary<Guid, string> titles = await dbContext.JobRequisitions
            .AsNoTracking()
            .Where(requisition => requisitionIds.Contains(requisition.Id))
            .ToDictionaryAsync(
                requisition => requisition.Id,
                requisition => requisition.PositionTitle,
                cancellationToken);

        // Everything in the diary around these, so the clash check can run in
        // memory — the panel is jsonb, which the query translator cannot look
        // inside, so overlapping panels cannot be found in SQL.
        DateTimeOffset from = interviews.Min(interview => interview.ScheduledAt).AddDays(-1);
        DateTimeOffset to = interviews.Max(Interviews.EndOf).AddDays(1);

        List<Interview> nearby = await dbContext.Interviews
            .AsNoTracking()
            .Where(interview => interview.Status == InterviewStatus.Scheduled
                && interview.ScheduledAt >= from
                && interview.ScheduledAt <= to)
            .ToListAsync(cancellationToken);

        List<Guid> panelIds =
        [
            .. interviews.SelectMany(interview => interview.Panel)
                .Concat(nearby.SelectMany(interview => interview.Panel))
                .Distinct(),
        ];

        Dictionary<Guid, string> people = panelIds.Count == 0
            ? []
            : await dbContext.Employees
                .AsNoTracking()
                .Where(employee => panelIds.Contains(employee.Id))
                .ToDictionaryAsync(
                    employee => employee.Id,
                    employee => employee.FirstName + " " + employee.LastName,
                    cancellationToken);

        return
        [
            .. interviews.Select(interview => new InterviewDto
            {
                Id = interview.Id,
                CandidateId = interview.CandidateId,
                CandidateName = candidates.GetValueOrDefault(interview.CandidateId) ?? "Unknown",
                RequisitionId = interview.RequisitionId,
                RequisitionTitle = titles.GetValueOrDefault(interview.RequisitionId),
                Round = interview.Round,
                ScheduledAt = interview.ScheduledAt,
                DurationMinutes = interview.DurationMinutes,
                Mode = interview.Mode,
                Panel = [.. interview.Panel],
                PanelNames =
                [
                    .. interview.Panel
                        .Select(member => people.GetValueOrDefault(member))
                        .OfType<string>(),
                ],
                Location = interview.Location,
                Status = interview.Status,

                // Named, not counted. "Cara Okafor is already booked" is
                // something somebody can act on; "1 clash" is not.
                Clashes =
                [
                    .. nearby
                        .Where(other => Interviews.Clashes(interview, other))
                        .SelectMany(other => other.Panel.Intersect(interview.Panel))
                        .Distinct()
                        .Select(member => people.GetValueOrDefault(member))
                        .OfType<string>(),
                ],
            }),
        ];
    }
}
