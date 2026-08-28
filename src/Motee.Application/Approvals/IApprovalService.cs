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
}

public sealed record ApprovalTemplateRequest
{
    public required string DocumentType { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public bool IsDefault { get; init; }

    public bool IsActive { get; init; } = true;

    public required IReadOnlyList<ApprovalTemplateStepRequest> Steps { get; init; }
}

public sealed record ApprovalTemplateStepRequest
{
    public required string Label { get; init; }

    public required ApproverResolver Approver { get; init; }

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

    public bool Succeeded => Outcome == ApprovalOutcome.Succeeded;

    public static ApprovalResult Failed(ApprovalOutcome outcome) => new() { Outcome = outcome };

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

    public required IReadOnlyList<ApprovalAction> AvailableActions { get; init; }

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

    public string? ResolvedName { get; init; }

    public required ApprovalStepStatus Status { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }

    public string? Note { get; init; }

    // Why nobody was asked. Written in words, so a skipped step explains itself.
    public string? SkippedReason { get; init; }
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
