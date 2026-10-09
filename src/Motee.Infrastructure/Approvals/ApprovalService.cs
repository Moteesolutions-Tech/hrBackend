using Microsoft.EntityFrameworkCore;
using Motee.Application.Approvals;
using Motee.Application.Common;
using Motee.Domain.Approvals;
using Motee.Domain.Authorization;
using Motee.Domain.Files;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Approvals;

internal sealed class ApprovalService(
    MoteeDbContext dbContext,
    IApproverResolution resolution,
    ApprovalAttachmentLinker attachmentLinks,
    IEnumerable<IApprovalObserver> observers,
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

        // Checked before anything is built. A chain that insists on a fit note should
        // refuse while the person who has it is still at the keyboard, not leave an
        // approver to discover the gap days later.
        if (AttachmentProblem(template.Attachments, request.FileIds) is ApprovalOutcome problem)
        {
            return ApprovalResult.Failed(problem, template.Attachments.Note);
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

        // A chain with nobody behind any of its pending steps can never move. Letting it
        // start would produce an approval that sits for ever with nobody able to act,
        // while the module that asked was told it succeeded — so the stall would only
        // surface when somebody noticed an ageing queue. Refusing says so at the one
        // moment there is still a person present to fix it.
        if (Unstartable(instance) is ApprovalStepInstance blocked)
        {
            Discard(instance);

            return ApprovalResult.Failed(ApprovalOutcome.Unstartable, blocked.SkippedReason);
        }

        if (!await AttachAsync(instance, request.FileIds, now, cancellationToken))
        {
            // A file id that is not an approval attachment belonging to this tenant.
            // Refused rather than dropped: silently discarding what somebody attached is
            // how a fit note goes missing and nobody finds out until the appeal.
            Discard(instance);

            return ApprovalResult.Failed(ApprovalOutcome.AttachmentNotAllowed);
        }

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

        // The access levels this person currently holds, for the role steps below.
        // Evaluated as part of the query rather than fetched first, so the queue reflects
        // their levels as they are at the moment they open the screen.
        IQueryable<Guid> myRoles = dbContext.UserAccessLevels
            .Where(assignment => assignment.UserId == userId)
            .Join(
                dbContext.AccessLevels.Where(level => level.Status == AccessLevelStatus.Active),
                assignment => assignment.AccessLevelId,
                level => level.Id,
                (assignment, _) => assignment.AccessLevelId);

        // Waiting on this person right now: a pending step naming them, or naming a role
        // they hold, on a run that is still live.
        //
        // A role step shows up for everybody who can clear it. That is the point of a
        // queue — but it does mean the same item appears in several people's lists until
        // one of them acts, which is the behaviour the screen wants and the reason
        // deciding is guarded again at the moment of the decision.
        IQueryable<ApprovalInstance> waiting = dbContext.ApprovalInstances
            .AsNoTracking()
            .Where(instance => instance.Status == ApprovalStatus.InProgress
                && dbContext.ApprovalStepInstances.Any(step =>
                    step.InstanceId == instance.Id
                    && step.Status == ApprovalStepStatus.Pending
                    && (step.ResolvedUserId == userId
                        || (step.ResolvedRoleId != null
                            && myRoles.Contains(step.ResolvedRoleId.Value)))
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

        // Only the person it is waiting on, or — for a role step — anyone holding that
        // access level right now. Without this, anyone with the module permission could
        // approve anyone's step and the chain's order would describe nothing.
        //
        // Role membership is checked here rather than trusted from submission time, which
        // is what lets somebody who joined the team this morning clear this morning's
        // queue, and stops somebody who has left from clearing anything.
        if (CurrentUser() is not Guid actor
            || !await CanActAsync(step, actor, cancellationToken))
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
        else
        {
            // The chain is moving on, so ask again about any step that had nobody when it
            // was submitted. A department head appointed while the manager was still
            // deciding should be found now, rather than the chain reaching that step and
            // stopping dead against a gap that has already been filled.
            await ReresolvePendingAsync(instance, cancellationToken);
        }

        instance.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        ApprovalDto decided = (await GetAsync(id, cancellationToken))!;

        await NotifyAsync(decided, cancellationToken);

        return ApprovalResult.Ok(decided);
    }

    // Tells whichever module owns this subject that its chain has moved. After the save,
    // never inside it: an observer that threw mid-transaction would roll back a decision
    // somebody had legitimately given.
    private async Task NotifyAsync(ApprovalDto approval, CancellationToken cancellationToken)
    {
        foreach (IApprovalObserver observer in observers
            .Where(candidate => candidate.SubjectType == approval.SubjectType))
        {
            await observer.OnSettledAsync(approval, cancellationToken);
        }
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
            // the submitter, and the new one is who should be asked. The role id comes
            // from the step's own snapshot, so a template edited in the meantime cannot
            // redirect this round to a different queue.
            ResolvedApprover resolved = await resolution.ResolveAsync(
                step.Approver, instance.SubjectEmployeeId, step.RoleId, cancellationToken);

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
    // Nothing to act on, and something still needing it. Both halves matter:
    //
    // A later required step with nobody behind it does not refuse the chain — the steps
    // before it are real work somebody should get on with, and blocking there with the
    // reason written on it is how the org chart gets fixed. That is what
    // ARequiredStepWithNobodyToAskBlocksAndSaysWhy pins down.
    //
    // A chain where no pending step has anybody behind it is different: it can never
    // move at all. And the all-optional case where every step was skipped is different
    // again — nothing is pending, so it settles as approved rather than being refused.
    //
    // Returns the offending step so the caller can pass on the reason it already wrote,
    // in words somebody can act on: "No line manager is recorded for this employee."
    private ApprovalStepInstance? Unstartable(ApprovalInstance instance)
    {
        List<ApprovalStepInstance> pending =
        [
            .. dbContext.ApprovalStepInstances.Local
                .Where(step => step.InstanceId == instance.Id
                    && step.Status == ApprovalStepStatus.Pending),
        ];

        if (pending.Count == 0)
        {
            return null;
        }

        return pending.Any(step => step.ResolvedUserId is not null || step.ResolvedRoleId is not null)
            ? null
            : pending[0];
    }

    // Nothing has been saved, but the context is scoped and may serve another call before
    // it is disposed. Leaving a half-built approval tracked would let an unrelated
    // SaveChanges commit the very rows this refused to create.
    private void Discard(ApprovalInstance instance)
    {
        foreach (ApprovalStepInstance step in dbContext.ApprovalStepInstances.Local
            .Where(step => step.InstanceId == instance.Id)
            .ToList())
        {
            dbContext.Entry(step).State = EntityState.Detached;
        }

        dbContext.Entry(instance).State = EntityState.Detached;
    }

    // What the rules say about what was sent. Null when there is nothing wrong.
    private static ApprovalOutcome? AttachmentProblem(
        AttachmentRules rules,
        IReadOnlyList<Guid> fileIds)
    {
        if (rules.Required && fileIds.Count == 0)
        {
            return ApprovalOutcome.AttachmentRequired;
        }

        return fileIds.Count > 0 && !rules.Permits
            ? ApprovalOutcome.AttachmentNotAllowed
            : null;
    }

    // Links files already uploaded through the files module. False when any id is not an
    // approval attachment in this tenant — the tenant filter answers the second half on
    // its own, so another company's file is simply not found.
    private async Task<bool> AttachAsync(
        ApprovalInstance instance,
        IReadOnlyList<Guid> fileIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0)
        {
            return true;
        }

        List<Guid> wanted = [.. fileIds.Distinct()];

        List<Guid> usable = await dbContext.StoredFiles
            .Where(file => wanted.Contains(file.Id)
                && file.Purpose == FilePurpose.ApprovalAttachment)
            .Select(file => file.Id)
            .ToListAsync(cancellationToken);

        if (usable.Count != wanted.Count)
        {
            return false;
        }

        foreach (Guid fileId in usable)
        {
            dbContext.ApprovalAttachments.Add(new ApprovalAttachment
            {
                Id = Guid.NewGuid(),
                InstanceId = instance.Id,
                FileId = fileId,

                // Stamped with the round it arrived in, so a note that came only after
                // the request was returned reads as exactly that.
                Round = instance.Round,
                UploadedByUserId = CurrentUser(),
                UploadedAt = now,
            });
        }

        return true;
    }

    public async Task<ApprovalResult> ReresolveAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ApprovalInstance? instance = await dbContext.ApprovalInstances
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (instance is null)
        {
            return ApprovalResult.Failed(ApprovalOutcome.NotFound);
        }

        // Only a live run. Asking a finished approval to look again would be asking it to
        // reopen, which is what resubmitting is for.
        if (instance.Status != ApprovalStatus.InProgress)
        {
            return ApprovalResult.Failed(ApprovalOutcome.NotAllowed);
        }

        int found = await ReresolvePendingAsync(instance, cancellationToken);

        if (found > 0)
        {
            // Recorded, because a step quietly acquiring an approver is exactly the kind
            // of change somebody will later need to account for.
            Record(
                instance,
                ApprovalEventTypes.Reresolved,
                null,
                $"{found} step(s) found an approver.",
                timeProvider.GetUtcNow());

            instance.UpdatedAt = timeProvider.GetUtcNow();

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return ApprovalResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    // Asks again for every pending step with nobody behind it, and reports how many found
    // somebody. Steps already waiting on a named person are left alone: re-resolving
    // those would move work away from somebody who is looking at it.
    private async Task<int> ReresolvePendingAsync(
        ApprovalInstance instance,
        CancellationToken cancellationToken)
    {
        List<ApprovalStepInstance> stuck = await dbContext.ApprovalStepInstances
            .Where(step => step.InstanceId == instance.Id
                && step.Status == ApprovalStepStatus.Pending
                && step.ResolvedUserId == null
                && step.ResolvedRoleId == null)
            .ToListAsync(cancellationToken);

        int found = 0;

        foreach (ApprovalStepInstance step in stuck)
        {
            ResolvedApprover resolved = await resolution.ResolveAsync(
                step.Approver, instance.SubjectEmployeeId, step.RoleId, cancellationToken);

            if (!resolved.Found)
            {
                // Still nobody. The reason is refreshed rather than left as it was, so a
                // step that was stuck for one reason and is now stuck for another says
                // which — "no head recorded" becoming "the head has left" is worth seeing.
                step.SkippedReason = resolved.Reason;
                continue;
            }

            step.SkippedReason = null;
            Apply(step, resolved);
            found++;
        }

        return found;
    }

    private async Task BuildStepsAsync(
        ApprovalInstance instance,
        IReadOnlyList<ApprovalTemplateStep> steps,
        CancellationToken cancellationToken)
    {
        foreach (ApprovalTemplateStep template in steps)
        {
            ResolvedApprover resolved = await resolution.ResolveAsync(
                template.Approver, instance.SubjectEmployeeId, template.RoleId, cancellationToken);

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
                RoleId = template.RoleId,
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
    // A step lands either on one person or on a queue, never on both — so this asks the
    // question that fits whichever kind it is.
    private async Task<bool> CanActAsync(
        ApprovalStepInstance step,
        Guid actor,
        CancellationToken cancellationToken) =>
        step.ResolvedRoleId is Guid roleId
            ? await resolution.HoldsRoleAsync(actor, roleId, cancellationToken)
            : step.ResolvedUserId == actor;

    private static void Apply(ApprovalStepInstance step, ResolvedApprover resolved)
    {
        if (resolved.Found)
        {
            step.ResolvedEmployeeId = resolved.EmployeeId;
            step.ResolvedUserId = resolved.UserId;
            step.ResolvedRoleId = resolved.RoleId;
            return;
        }

        step.ResolvedEmployeeId = null;
        step.ResolvedUserId = null;
        step.ResolvedRoleId = null;
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

                // A person's name, or the access level's. One or the other is set, and a
                // screen shows "with Ada Okafor" or "with HR Admin" from the same field
                // rather than branching on which kind of step it is.
                ResolvedName = step.ResolvedRoleId != null
                    ? dbContext.AccessLevels
                        .Where(level => level.Id == step.ResolvedRoleId)
                        .Select(level => level.Name)
                        .FirstOrDefault()
                    : dbContext.Employees
                        .Where(employee => employee.Id == step.ResolvedEmployeeId)
                        .Select(employee => employee.FirstName + " " + employee.LastName)
                        .FirstOrDefault(),
                ResolvedRoleId = step.ResolvedRoleId,
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

        List<ApprovalAttachmentDto> attachments = await dbContext.ApprovalAttachments
            .AsNoTracking()
            .Where(attachment => attachment.InstanceId == instance.Id)

            // Newest round first: what arrived most recently is what an approver looking
            // at this now has not yet seen.
            .OrderByDescending(attachment => attachment.Round)
            .ThenByDescending(attachment => attachment.UploadedAt)
            .Select(attachment => new ApprovalAttachmentDto
            {
                Id = attachment.Id,
                FileId = attachment.FileId,
                FileName = dbContext.StoredFiles
                    .Where(file => file.Id == attachment.FileId)
                    .Select(file => file.FileName)
                    .FirstOrDefault() ?? "Unknown",
                ContentType = dbContext.StoredFiles
                    .Where(file => file.Id == attachment.FileId)
                    .Select(file => file.ContentType)
                    .FirstOrDefault() ?? "application/octet-stream",
                SizeBytes = dbContext.StoredFiles
                    .Where(file => file.Id == attachment.FileId)
                    .Select(file => file.SizeBytes)
                    .FirstOrDefault(),
                UploadedByName = dbContext.Users
                    .Where(user => user.Id == attachment.UploadedByUserId)
                    .Select(user => user.FirstName + " " + user.LastName)
                    .FirstOrDefault(),
                Round = attachment.Round,
                UploadedAt = attachment.UploadedAt,

                // Signed after the query. A link with a lifetime cannot come out of a
                // projection.
                Url = null,
            })
            .ToListAsync(cancellationToken);

        if (attachments.Count > 0)
        {
            IReadOnlyDictionary<Guid, string> urls = await attachmentLinks.UrlsForAsync(
                attachments.Select(attachment => attachment.FileId), cancellationToken);

            attachments =
            [
                .. attachments.Select(attachment =>
                    urls.TryGetValue(attachment.FileId, out string? url)
                        ? attachment with { Url = url }
                        : attachment),
            ];
        }

        // The rules are read through from the template rather than snapshotted onto the
        // instance. They are validation, applied at the moment somebody submits — unlike
        // the steps, which are state and must not shift under a running chain.
        AttachmentRules rules = await dbContext.ApprovalTemplates
            .AsNoTracking()
            .Where(template => template.Id == instance.TemplateId)
            .Select(template => template.Attachments)
            .FirstOrDefaultAsync(cancellationToken) ?? AttachmentRules.None;

        // The step it is stopped against, if it is stopped: pending, and with neither a
        // person nor a queue behind it.
        ApprovalStepDto? blocked = steps.FirstOrDefault(step =>
            step.Status == ApprovalStepStatus.Pending
            && step.ResolvedEmployeeId is null
            && step.ResolvedRoleId is null);

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

            // Live, and the step it is waiting on has nobody behind it. Derived rather
            // than stored: the thing that fixes it — appointing a head, assigning a level
            // — happens elsewhere entirely and would never come back to clear a flag.
            IsBlocked = instance.Status == ApprovalStatus.InProgress && blocked is not null,
            BlockedReason = blocked?.SkippedReason,
            Attachments = attachments,
            AttachmentRules = rules,
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
