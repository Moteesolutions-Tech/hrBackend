using Microsoft.EntityFrameworkCore;
using Motee.Application.Approvals;
using Motee.Application.Common;
using Motee.Domain.Approvals;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Approvals;

internal sealed class ApprovalService(
    MoteeDbContext dbContext,
    IApproverResolution resolution,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IApprovalService
{
    public async Task<ApprovalResult> StartAsync(
        StartApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ApprovalTemplate? template = request.TemplateId is Guid templateId
            ? await dbContext.ApprovalTemplates
                .FirstOrDefaultAsync(candidate => candidate.Id == templateId, cancellationToken)
            : await dbContext.ApprovalTemplates
                .Where(candidate => candidate.DocumentType == request.DocumentType
                    && candidate.IsDefault
                    && candidate.IsActive)
                .FirstOrDefaultAsync(cancellationToken);

        if (template is null)
        {
            return ApprovalResult.Failed(ApprovalOutcome.TemplateNotFound);
        }

        List<ApprovalTemplateStep> steps = await dbContext.ApprovalTemplateSteps
            .AsNoTracking()
            .Where(step => step.TemplateId == template.Id)
            .OrderBy(step => step.Sequence)
            .ToListAsync(cancellationToken);

        // A chain with no steps approves nothing and waits for nobody. Better to refuse
        // than to create an instance that is complete the instant it exists.
        if (steps.Count == 0)
        {
            return ApprovalResult.Failed(ApprovalOutcome.Unstartable);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        ApprovalInstance instance = new()
        {
            Id = Guid.NewGuid(),
            TemplateId = template.Id,
            DocumentType = template.DocumentType,
            SubjectType = request.SubjectType,
            SubjectId = request.SubjectId,
            SubjectEmployeeId = request.SubjectEmployeeId,
            Status = ApprovalStatus.InProgress,
            SubmittedByUserId = CurrentUser(),
            SubmittedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.ApprovalInstances.Add(instance);

        await BuildStepsAsync(instance, steps, cancellationToken);

        // Every step resolved to nobody and none was required, so the whole chain is
        // skippable. Approving it immediately is right — but silently creating a
        // "fully approved" record nobody touched would misrepresent it, so the events
        // below record each skip and the reason.
        await SettleAsync(instance, cancellationToken);

        Record(instance, ApprovalEventTypes.Submitted, null, null, now);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ApprovalResult.Ok((await GetAsync(instance.Id, cancellationToken))!);
    }

    public async Task<ApprovalDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ApprovalInstance? instance = await dbContext.ApprovalInstances
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return instance is null ? null : await ProjectAsync(instance, cancellationToken);
    }

    public async Task<IReadOnlyList<ApprovalDto>> ForSubjectAsync(
        string subjectType,
        Guid subjectId,
        CancellationToken cancellationToken = default)
    {
        List<ApprovalInstance> instances = await dbContext.ApprovalInstances
            .AsNoTracking()
            .Where(instance => instance.SubjectType == subjectType && instance.SubjectId == subjectId)
            .OrderByDescending(instance => instance.CreatedAt)
            .ToListAsync(cancellationToken);

        List<ApprovalDto> results = [];

        foreach (ApprovalInstance instance in instances)
        {
            results.Add(await ProjectAsync(instance, cancellationToken));
        }

        return results;
    }

    public async Task<IReadOnlyDictionary<Guid, ApprovalDto>> LatestForSubjectsAsync(
        string subjectType,
        IReadOnlyCollection<Guid> subjectIds,
        CancellationToken cancellationToken = default)
    {
        if (subjectIds.Count == 0)
        {
            return new Dictionary<Guid, ApprovalDto>();
        }

        // One row per subject, decided in the database rather than by fetching every
        // round and discarding all but the newest — a record returned and resubmitted
        // three times should not cost three times as much to display.
        List<ApprovalInstance> latest = await dbContext.ApprovalInstances
            .AsNoTracking()
            .Where(instance => instance.SubjectType == subjectType
                && subjectIds.Contains(instance.SubjectId))
            .GroupBy(instance => instance.SubjectId)
            .Select(group => group
                .OrderByDescending(instance => instance.CreatedAt)
                .First())
            .ToListAsync(cancellationToken);

        Dictionary<Guid, ApprovalDto> results = [];

        foreach (ApprovalInstance instance in latest)
        {
            results[instance.SubjectId] = await ProjectAsync(instance, cancellationToken);
        }

        return results;
    }

    public async Task<PagedResult<ApprovalDto>> MyQueueAsync(
        PagedQuery query,
        CancellationToken cancellationToken = default)
    {
        if (CurrentUser() is not Guid userId)
        {
            return Empty(query);
        }

        // Waiting on this person right now: a pending step resolved to them, on a run
        // that is still live.
        IQueryable<ApprovalInstance> waiting = dbContext.ApprovalInstances
            .AsNoTracking()
            .Where(instance => instance.Status == ApprovalStatus.InProgress
                && dbContext.ApprovalStepInstances.Any(step =>
                    step.InstanceId == instance.Id
                    && step.Status == ApprovalStepStatus.Pending
                    && step.ResolvedUserId == userId
                    && step.Sequence == dbContext.ApprovalStepInstances
                        .Where(pending => pending.InstanceId == instance.Id
                            && pending.Status == ApprovalStepStatus.Pending)
                        .Min(pending => pending.Sequence)));

        int total = await waiting.CountAsync(cancellationToken);

        List<ApprovalInstance> page = await waiting
            .OrderBy(instance => instance.SubmittedAt)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        List<ApprovalDto> items = [];

        foreach (ApprovalInstance instance in page)
        {
            items.Add(await ProjectAsync(instance, cancellationToken));
        }

        return new PagedResult<ApprovalDto>
        {
            Items = items,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<ApprovalResult> DecideAsync(
        Guid id,
        ApprovalStepStatus decision,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        ApprovalInstance? instance = await dbContext.ApprovalInstances
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (instance is null)
        {
            return ApprovalResult.Failed(ApprovalOutcome.NotFound);
        }

        ApprovalStepInstance? step = await CurrentStepAsync(instance.Id, cancellationToken);

        if (step is null || !ApprovalChainRules.CanDecide(step.Status, instance.Status))
        {
            return ApprovalResult.Failed(ApprovalOutcome.NotAllowed);
        }

        // Only the person it is waiting on. Without this, anyone holding the module
        // permission could approve anyone's step, and the chain's order would describe
        // nothing.
        if (step.ResolvedUserId != CurrentUser())
        {
            return ApprovalResult.Failed(ApprovalOutcome.NotTheApprover);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        step.Status = decision;
        step.DecidedByUserId = CurrentUser();
        step.DecidedAt = now;
        step.Note = Trimmed(note);

        Record(instance, EventFor(decision), step.Sequence, Trimmed(note), now);

        bool isLast = !await dbContext.ApprovalStepInstances
            .AnyAsync(
                other => other.InstanceId == instance.Id
                    && other.Sequence > step.Sequence
                    && other.Status == ApprovalStepStatus.Pending,
                cancellationToken);

        if (ApprovalChainRules.Outcome(decision, isLast) is ApprovalStatus outcome)
        {
            instance.Status = outcome;
            instance.DecidedAt = now;
        }

        instance.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return ApprovalResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    public async Task<ApprovalResult> ResubmitAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ApprovalInstance? instance = await dbContext.ApprovalInstances
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (instance is null)
        {
            return ApprovalResult.Failed(ApprovalOutcome.NotFound);
        }

        if (ApprovalLifecycle.Next(instance.Status, ApprovalAction.Resubmit)
            is not ApprovalStatus next)
        {
            return ApprovalResult.Failed(ApprovalOutcome.NotAllowed);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        // Every step starts again, including ones already approved. Resuming from the
        // step that returned it would mean the approvals already given were given to a
        // different document — whoever signed the original never saw what went through.
        List<ApprovalStepInstance> steps = await dbContext.ApprovalStepInstances
            .Where(step => step.InstanceId == instance.Id)
            .ToListAsync(cancellationToken);

        foreach (ApprovalStepInstance step in steps)
        {
            step.Status = ApprovalStepStatus.Pending;
            step.DecidedByUserId = null;
            step.DecidedAt = null;
            step.Note = null;
            step.SkippedReason = null;

            // Re-resolved, not reused: the manager may have changed while it sat with
            // the submitter, and the new one is who should be asked.
            ResolvedApprover resolved = await resolution.ResolveAsync(
                step.Approver, instance.SubjectEmployeeId, cancellationToken);

            Apply(step, resolved);
        }

        instance.Status = next;
        instance.Round += 1;
        instance.DecidedAt = null;
        instance.UpdatedAt = now;

        Record(instance, ApprovalEventTypes.Resubmitted, null, null, now);

        await SettleAsync(instance, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ApprovalResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    public async Task<ApprovalResult> CancelAsync(
        Guid id,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ApprovalInstance? instance = await dbContext.ApprovalInstances
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (instance is null)
        {
            return ApprovalResult.Failed(ApprovalOutcome.NotFound);
        }

        if (ApprovalLifecycle.Next(instance.Status, ApprovalAction.Cancel)
            is not ApprovalStatus next)
        {
            return ApprovalResult.Failed(ApprovalOutcome.NotAllowed);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        instance.Status = next;
        instance.DecidedAt = now;
        instance.UpdatedAt = now;

        Record(instance, ApprovalEventTypes.Cancelled, null, Trimmed(reason), now);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ApprovalResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    // Snapshots the template into the run and works out who each step lands on.
    private async Task BuildStepsAsync(
        ApprovalInstance instance,
        IReadOnlyList<ApprovalTemplateStep> steps,
        CancellationToken cancellationToken)
    {
        foreach (ApprovalTemplateStep template in steps)
        {
            ResolvedApprover resolved = await resolution.ResolveAsync(
                template.Approver, instance.SubjectEmployeeId, cancellationToken);

            ApprovalStepInstance step = new()
            {
                Id = Guid.NewGuid(),
                InstanceId = instance.Id,
                TemplateStepId = template.Id,
                Sequence = template.Sequence,

                // Copied, not read through. This is the snapshot: the label and the rule
                // as they were when the person was asked.
                Label = template.Label,
                Approver = template.Approver,
                Required = template.Required,
                Status = ApprovalStepStatus.Pending,
            };

            Apply(step, resolved);

            dbContext.ApprovalStepInstances.Add(step);
        }
    }

    // A step nobody could be found for is skipped when it is optional, and left pending
    // with the reason when it is required — so a required approval never quietly
    // approves itself, and an optional one never blocks.
    private static void Apply(ApprovalStepInstance step, ResolvedApprover resolved)
    {
        if (resolved.Found)
        {
            step.ResolvedEmployeeId = resolved.EmployeeId;
            step.ResolvedUserId = resolved.UserId;
            return;
        }

        step.ResolvedEmployeeId = null;
        step.ResolvedUserId = null;
        step.SkippedReason = resolved.Reason;

        if (!step.Required)
        {
            step.Status = ApprovalStepStatus.Skipped;
        }
    }

    // After building or rebuilding steps: if everything resolvable is already settled,
    // the run is finished. Without this a chain of optional unresolved steps would sit
    // in progress with nothing pending and nobody to ask.
    private async Task SettleAsync(
        ApprovalInstance instance,
        CancellationToken cancellationToken)
    {
        bool anythingPending = dbContext.ApprovalStepInstances.Local
            .Any(step => step.InstanceId == instance.Id
                && step.Status == ApprovalStepStatus.Pending)
            || await dbContext.ApprovalStepInstances
                .AnyAsync(
                    step => step.InstanceId == instance.Id
                        && step.Status == ApprovalStepStatus.Pending,
                    cancellationToken);

        if (!anythingPending)
        {
            instance.Status = ApprovalStatus.Approved;
            instance.DecidedAt = timeProvider.GetUtcNow();
        }
    }

    private Task<ApprovalStepInstance?> CurrentStepAsync(
        Guid instanceId,
        CancellationToken cancellationToken) =>
        dbContext.ApprovalStepInstances
            .Where(step => step.InstanceId == instanceId
                && step.Status == ApprovalStepStatus.Pending)
            .OrderBy(step => step.Sequence)
            .FirstOrDefaultAsync(cancellationToken);

    private void Record(
        ApprovalInstance instance,
        string type,
        int? stepOrder,
        string? note,
        DateTimeOffset at) =>
        dbContext.ApprovalEvents.Add(new ApprovalEvent
        {
            Id = Guid.NewGuid(),
            InstanceId = instance.Id,
            Type = type,
            StepOrder = stepOrder,
            ActorUserId = CurrentUser(),
            ActorName = requestContext.UserEmail,
            Note = note,
            At = at,
        });

    private static string EventFor(ApprovalStepStatus decision) => decision switch
    {
        ApprovalStepStatus.Approved => ApprovalEventTypes.Approved,
        ApprovalStepStatus.Rejected => ApprovalEventTypes.Rejected,
        ApprovalStepStatus.Returned => ApprovalEventTypes.Returned,
        ApprovalStepStatus.Skipped => ApprovalEventTypes.Skipped,
        _ => ApprovalEventTypes.Approved,
    };

    private async Task<ApprovalDto> ProjectAsync(
        ApprovalInstance instance,
        CancellationToken cancellationToken)
    {
        List<ApprovalStepDto> steps = await dbContext.ApprovalStepInstances
            .AsNoTracking()
            .Where(step => step.InstanceId == instance.Id)
            .OrderBy(step => step.Sequence)
            .Select(step => new ApprovalStepDto
            {
                Id = step.Id,
                Sequence = step.Sequence,
                Label = step.Label,
                Approver = step.Approver,
                Required = step.Required,
                ResolvedEmployeeId = step.ResolvedEmployeeId,
                ResolvedName = dbContext.Employees
                    .Where(employee => employee.Id == step.ResolvedEmployeeId)
                    .Select(employee => employee.FirstName + " " + employee.LastName)
                    .FirstOrDefault(),
                Status = step.Status,
                DecidedAt = step.DecidedAt,
                Note = step.Note,
                SkippedReason = step.SkippedReason,
            })
            .ToListAsync(cancellationToken);

        List<ApprovalEventDto> history = await dbContext.ApprovalEvents
            .AsNoTracking()
            .Where(entry => entry.InstanceId == instance.Id)
            .OrderBy(entry => entry.At)
            .Select(entry => new ApprovalEventDto
            {
                Id = entry.Id,
                Type = entry.Type,
                StepOrder = entry.StepOrder,
                ActorName = entry.ActorName,
                Note = entry.Note,
                At = entry.At,
            })
            .ToListAsync(cancellationToken);

        return new ApprovalDto
        {
            Id = instance.Id,
            DocumentType = instance.DocumentType,
            SubjectType = instance.SubjectType,
            SubjectId = instance.SubjectId,
            SubjectEmployeeId = instance.SubjectEmployeeId,
            Status = instance.Status,
            Round = instance.Round,
            Steps = steps,
            CurrentStep = instance.Status == ApprovalStatus.InProgress
                ? steps.FirstOrDefault(step => step.Status == ApprovalStepStatus.Pending)
                : null,
            AvailableActions = ApprovalLifecycle.AvailableFrom(instance.Status),
            History = history,
            SubmittedAt = instance.SubmittedAt,
            DecidedAt = instance.DecidedAt,
        };
    }

    private static PagedResult<ApprovalDto> Empty(PagedQuery query) => new()
    {
        Items = [],
        Page = query.Page,
        PageSize = query.PageSize,
        TotalItems = 0,
    };

    private Guid? CurrentUser() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
