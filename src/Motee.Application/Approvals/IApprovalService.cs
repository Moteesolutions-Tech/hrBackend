using Motee.Application.Common;
using Motee.Domain.Approvals;

namespace Motee.Application.Approvals;

// Templates: what a company's approval chains look like. Managed on their own screen and
// changed rarely, which is why editing one must never reach an approval already running.
public interface IApprovalTemplateService
{
    Task<IReadOnlyList<ApprovalTemplateDto>> ListAsync(
        string? documentType = null,
        CancellationToken cancellationToken = default);

    Task<ApprovalTemplateDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ApprovalTemplateResult> CreateAsync(
        ApprovalTemplateRequest request,
        CancellationToken cancellationToken = default);

    Task<ApprovalTemplateResult> UpdateAsync(
        Guid id,
        ApprovalTemplateRequest request,
        CancellationToken cancellationToken = default);

    Task<ApprovalTemplateOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    // The access levels a Role step can name. Active ones only — a step pointing at a
    // deactivated level resolves to nobody, so offering one would be offering a choice
    // that quietly does not work.
    Task<IReadOnlyList<ApprovalRoleDto>> RolesAsync(CancellationToken cancellationToken = default);
}

public sealed record ApprovalRoleDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    // How many people could actually act. Zero is worth showing at the point of choosing:
    // a step handed to an empty level is one nobody will ever see.
    public required int Holders { get; init; }
}

// Instances: one run of a chain against one thing. Started by whichever module needs an
// approval, which never has to know how chains are configured.
public interface IApprovalService
{
    // Called by a module — onboarding, offboarding, leave. Resolves every step now and
    // snapshots the template into the instance, so a later template edit cannot change
    // what is already in flight.
    Task<ApprovalResult> StartAsync(
        StartApprovalRequest request,
        CancellationToken cancellationToken = default);

    Task<ApprovalDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // Everything running against one record, so a module can show "this onboarding is
    // waiting on the department head" without knowing the engine's tables.
    Task<IReadOnlyList<ApprovalDto>> ForSubjectAsync(
        string subjectType,
        Guid subjectId,
        CancellationToken cancellationToken = default);

    // The same question for a page of records, in one round trip. Any module listing
    // things under approval needs this — without it a page of twenty-five joiners is
    // twenty-five queries, and the fix would otherwise be written again per module.
    //
    // The newest run per subject, because that is the one a screen means by "the review":
    // an earlier round that was returned is history, and the engine keeps it as history.
    Task<IReadOnlyDictionary<Guid, ApprovalDto>> LatestForSubjectsAsync(
        string subjectType,
        IReadOnlyCollection<Guid> subjectIds,
        CancellationToken cancellationToken = default);

    // What is sitting in the current user's queue. The screen everyone actually opens.
    Task<PagedResult<ApprovalDto>> MyQueueAsync(
        PagedQuery query,
        CancellationToken cancellationToken = default);

    Task<ApprovalResult> DecideAsync(
        Guid id,
        ApprovalStepStatus decision,
        string? note = null,
        CancellationToken cancellationToken = default);

    Task<ApprovalResult> ResubmitAsync(Guid id, CancellationToken cancellationToken = default);

    // Ask again who a blocked step should go to.
    //
    // Steps resolve at submission, so a chain whose required step found nobody stays
    // stuck even after somebody fixes the thing that caused it — appoints the department
    // head, gives the new starter an account, assigns somebody to the level. Nothing
    // else can unstick it: the step has no approver, so no decision path ever runs
    // against it again, and the only alternative would be cancelling and losing the
    // approvals already given.
    //
    // Only pending steps with nobody behind them are touched. A step already waiting on
    // a named person keeps that person, so this can never move work away from somebody
    // who is looking at it.
    Task<ApprovalResult> ReresolveAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ApprovalResult> CancelAsync(
        Guid id,
        string? reason = null,
        CancellationToken cancellationToken = default);
}

public enum ApprovalTemplateOutcome
{
    Succeeded,
    NotFound,
    DuplicateName,

    // Shipped by us. Copyable and deactivatable, never editable — a company that changed
    // one would leave older instances pointing at something that no longer matches.
    SystemTemplate,

    // Approvals are running against it. Deleting would orphan them.
    InUse,

    NoSteps,

    // A Role step naming no access level, or naming one that does not exist here. Caught
    // at save rather than left to fail at resolution, where it would present as an
    // approval mysteriously skipping a step somebody configured on purpose.
    RoleMissing,
}

public enum ApprovalOutcome
{
    Succeeded,
    NotFound,
    TemplateNotFound,

    // The template has no steps, or none that could be resolved to anybody. Starting it
    // would create an approval nobody can act on, which is worse than refusing.
    Unstartable,

    // The lifecycle refuses: deciding a draft, resubmitting something approved.
    NotAllowed,

    // Not the person the current step is waiting on.
    NotTheApprover,

    // The chain insists on evidence and none was given. Refused at submission rather
    // than left for an approver to notice, because the person who can fix it is the one
    // still at the keyboard.
    AttachmentRequired,

    // Files were sent to a chain that does not take them, or a file id that is not an
    // approval attachment in this tenant. Refused rather than dropped: silently
    // discarding what somebody attached is how a fit note goes missing.
    AttachmentNotAllowed,
}

public sealed record ApprovalTemplateRequest
{
    public required string DocumentType { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public bool IsDefault { get; init; }

    public bool IsActive { get; init; } = true;

    public AttachmentRules Attachments { get; init; } = AttachmentRules.None;

    public required IReadOnlyList<ApprovalTemplateStepRequest> Steps { get; init; }
}

public sealed record ApprovalTemplateStepRequest
{
    public required string Label { get; init; }

    public required ApproverResolver Approver { get; init; }

    // Required when Approver is Role, ignored otherwise. The access level that answers
    // this step — "someone in HR" rather than a named person.
    public Guid? RoleId { get; init; }

    public bool Required { get; init; } = true;
}

public sealed record StartApprovalRequest
{
    public required string DocumentType { get; init; }

    // What is being approved. The engine stores these and hands them back; it never
    // interprets them, so a new module needs no change here.
    public required string SubjectType { get; init; }

    public required Guid SubjectId { get; init; }

    // Who the chain is about. The positional resolvers need it — the line manager of
    // whom, the head of which department.
    public Guid? SubjectEmployeeId { get; init; }

    // Omitted means the default template for this document type.
    public Guid? TemplateId { get; init; }

    // Files already uploaded through the files module, with purpose ApprovalAttachment.
    // Referenced here rather than posted as bytes, for the same reason avatars are: the
    // upload has its own size and type checks, and a chain start should not also be a
    // multipart body.
    public IReadOnlyList<Guid> FileIds { get; init; } = [];
}

public sealed record ApprovalTemplateResult
{
    public required ApprovalTemplateOutcome Outcome { get; init; }

    public ApprovalTemplateDto? Template { get; init; }

    public bool Succeeded => Outcome == ApprovalTemplateOutcome.Succeeded;

    public static ApprovalTemplateResult Failed(ApprovalTemplateOutcome outcome) =>
        new() { Outcome = outcome };

    public static ApprovalTemplateResult Ok(ApprovalTemplateDto template) =>
        new() { Outcome = ApprovalTemplateOutcome.Succeeded, Template = template };
}

public sealed record ApprovalResult
{
    public required ApprovalOutcome Outcome { get; init; }

    public ApprovalDto? Approval { get; init; }

    // Why, when the engine knows something the outcome alone cannot say — "No line
    // manager is recorded for this employee" rather than "unstartable". The module that
    // asked can pass it straight on; whoever has to fix it needs the specific sentence,
    // not the category.
    public string? Reason { get; init; }

    public bool Succeeded => Outcome == ApprovalOutcome.Succeeded;

    public static ApprovalResult Failed(ApprovalOutcome outcome, string? reason = null) =>
        new() { Outcome = outcome, Reason = reason };

    public static ApprovalResult Ok(ApprovalDto approval) =>
        new() { Outcome = ApprovalOutcome.Succeeded, Approval = approval };
}

public sealed record ApprovalTemplateDto
{
    public required Guid Id { get; init; }

    public required string DocumentType { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required bool IsDefault { get; init; }

    public required bool IsSystem { get; init; }

    public required bool IsActive { get; init; }

    public required AttachmentRules Attachments { get; init; }

    public required IReadOnlyList<ApprovalTemplateStepDto> Steps { get; init; }

    // Counted, so a screen can warn before editing something with approvals running
    // against it.
    public required int RunningInstances { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ApprovalTemplateStepDto
{
    public required Guid Id { get; init; }

    public required int Sequence { get; init; }

    public required string Label { get; init; }

    public required ApproverResolver Approver { get; init; }

    public Guid? RoleId { get; init; }

    // The access level's name, so a chain reads "HR Admin approves" without the client
    // holding its own copy of the role list.
    public string? RoleName { get; init; }

    public required bool Required { get; init; }
}

public sealed record ApprovalDto
{
    public required Guid Id { get; init; }

    public required string DocumentType { get; init; }

    public required string SubjectType { get; init; }

    public required Guid SubjectId { get; init; }

    public Guid? SubjectEmployeeId { get; init; }

    public required ApprovalStatus Status { get; init; }

    public required int Round { get; init; }

    public required IReadOnlyList<ApprovalStepDto> Steps { get; init; }

    // The step waiting on somebody, or null when nothing is. What a screen shows as
    // "with Ada Okafor".
    public ApprovalStepDto? CurrentStep { get; init; }

    // Waiting on a step that has nobody behind it. Distinct from simply being in
    // progress: nothing will happen here without somebody fixing the org chart and
    // asking the chain to look again, and a screen that showed this as ordinary
    // "in progress" would leave it ageing quietly in nobody's queue.
    public required bool IsBlocked { get; init; }

    // The step's own words: "No head is recorded for this department."
    public string? BlockedReason { get; init; }

    public required IReadOnlyList<ApprovalAction> AvailableActions { get; init; }

    // What was attached, newest round first. Every approver in the chain sees the same
    // set — that is the point of attaching to the run rather than to a step.
    public required IReadOnlyList<ApprovalAttachmentDto> Attachments { get; init; }

    // The chain's own rules, so a screen can say what is expected before somebody
    // submits rather than after it is refused.
    public required AttachmentRules AttachmentRules { get; init; }

    public required IReadOnlyList<ApprovalEventDto> History { get; init; }

    public DateTimeOffset? SubmittedAt { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }
}

public sealed record ApprovalStepDto
{
    public required Guid Id { get; init; }

    public required int Sequence { get; init; }

    public required string Label { get; init; }

    public required ApproverResolver Approver { get; init; }

    public required bool Required { get; init; }

    public Guid? ResolvedEmployeeId { get; init; }

    // Set instead of ResolvedEmployeeId when a role answers this step. The two are
    // mutually exclusive — a step waits on one person or on a queue, never both.
    public Guid? ResolvedRoleId { get; init; }

    // Whichever of the two was found: the person's name, or the access level's.
    public string? ResolvedName { get; init; }

    public required ApprovalStepStatus Status { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }

    public string? Note { get; init; }

    // Why nobody was asked. Written in words, so a skipped step explains itself.
    public string? SkippedReason { get; init; }

    // Set when a delegation redirected this step. Lets a screen say "Approved by Cara, on
    // behalf of Ada — Annual leave, 3–10 July" rather than leaving a decision that looks
    // like it came from the wrong person.
    public StepDelegation? Delegation { get; init; }
}

public sealed record ApprovalAttachmentDto
{
    public required Guid Id { get; init; }

    public required Guid FileId { get; init; }

    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public required long SizeBytes { get; init; }

    // Signed on read, like every other file link here. The bucket is private, so a URL
    // kept in the database would be a broken link with a delay on it.
    public string? Url { get; init; }

    public required int Round { get; init; }

    public string? UploadedByName { get; init; }

    public required DateTimeOffset UploadedAt { get; init; }
}

public sealed record ApprovalEventDto
{
    public required Guid Id { get; init; }

    public required string Type { get; init; }

    public int? StepOrder { get; init; }

    public string? ActorName { get; init; }

    public string? Note { get; init; }

    public required DateTimeOffset At { get; init; }
}
