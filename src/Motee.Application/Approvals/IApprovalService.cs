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

    // The request types somebody may raise from the intake form, with the fields each
    // one asks for.
    //
    // Only types with a chain configured and no module behind them. A type with no
    // chain has nobody to send the request to; a module-driven type has its own form,
    // and raising one here would create an approval with no record behind it.
    Task<IReadOnlyList<RaisableTypeDto>> DocumentTypesAsync(
        CancellationToken cancellationToken = default);

    // Raising a request directly, with no module creating a record first. For these the
    // approval is the record: the title, the fields and the decision are the whole
    // thing, which is why the six document types nothing else starts are exactly the
    // ones this serves.
    Task<ApprovalResult> RaiseAsync(
        RaiseApprovalRequest request,
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

    // The register: every approval in the tenant, whoever raised it and whatever became
    // of it. Not a queue — an approval nobody is waiting on still belongs here, and that
    // is exactly the one no other read could find.
    Task<PagedResult<ApprovalDto>> ListAsync(
        ApprovalQuery query,
        CancellationToken cancellationToken = default);

    // What the signed-in account raised, in any status. "What happened to the leave I
    // asked for" is the question, and the answer is usually something already decided —
    // so unlike the queue, this does not stop showing an approval once it is settled.
    Task<PagedResult<ApprovalDto>> MineAsync(
        PagedQuery query,
        CancellationToken cancellationToken = default);

    // signatureFileId is optional and only meaningful on an approval: a chain may ask
    // the reviewer to sign. Refusing is not signed, so it carries none.
    Task<ApprovalResult> DecideAsync(
        Guid id,
        ApprovalStepStatus decision,
        string? note = null,
        Guid? signatureFileId = null,
        CancellationToken cancellationToken = default);

    // A note without a decision — an approver asking for a receipt, a submitter
    // answering. Lands in the same history as the decisions, so the exchange reads as
    // one sequence rather than two lists interleaved by timestamp.
    //
    // Open to anybody party to the chain: the person who raised it and anybody a step
    // resolved to. Not behind the module permission, because an approver need not hold
    // it to have been asked to decide — the same reasoning as DecideAsync.
    Task<ApprovalResult> CommentAsync(
        Guid id,
        string message,
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

// What the register can be narrowed to. Every filter is optional, and the unfiltered
// query is the screen's default view.
public sealed record ApprovalQuery : PagedQuery
{
    public ApprovalStatus? Status { get; init; }

    public string? DocumentType { get; init; }

    // Who raised it, named by employee rather than by account. The engine records the
    // account that submitted — but the screen picks a person from the employee list, so
    // the resolution across happens here rather than in every client.
    public Guid? RequesterEmployeeId { get; init; }

    // Who it is about, which is a different person from the one who raised it whenever a
    // manager submits something on somebody's behalf.
    public Guid? SubjectEmployeeId { get; init; }

    // Against the submission date. Both ends inclusive.
    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }
}

public sealed record RaisableTypeDto
{
    public required string Key { get; init; }

    // Humanised from the key — "Asset request" — so the form has something to show
    // without holding its own vocabulary. A tenant's own category reads from its own
    // words rather than a lookup that would not have it.
    public required string Label { get; init; }

    // What this type asks for beyond a title and a reason. Empty is a valid answer: a
    // generic request is a title, a reason and an attachment.
    public required IReadOnlyList<IntakeField> Fields { get; init; }

    // Whether the chain takes evidence, and whether it insists on it. Sent so the form
    // can say what is expected before somebody submits rather than after it is refused.
    public required AttachmentRules Attachments { get; init; }
}

public sealed record RaiseApprovalRequest
{
    public required string DocumentType { get; init; }

    // What the request is, in the requester's own words. Required here, unlike on a
    // module-driven chain where the module supplies it: an intake request with no title
    // is a row in the register saying nothing.
    public required string Title { get; init; }

    public string? Summary { get; init; }

    // The fields the type asked for. Checked against the schema, so a key nobody asked
    // for is refused rather than stored — the map is rendered back to the approver, and
    // anything accepted here appears on the request as though it had been requested.
    public IReadOnlyDictionary<string, string?> Fields { get; init; } =
        new Dictionary<string, string?>();

    // Files already uploaded with purpose ApprovalAttachment.
    public IReadOnlyList<Guid> FileIds { get; init; } = [];

    // Omitted means the default chain for this type.
    public Guid? TemplateId { get; init; }
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

    // What the request is, in the module's own words: "Annual leave", "3–7 Mar · 5
    // days". Both optional, and both supplied by the module rather than worked out
    // here, because the engine stores a subject type and a subject id and never
    // interprets them — it does not know what an onboarding record is, and making it
    // know would mean editing the engine for every new module.
    //
    // Carried opaquely: stored, handed back, never parsed.
    public string? Title { get; init; }

    public string? Summary { get; init; }

    // The intake form's field map, for a request raised with no module behind it.
    // Empty for every module-driven chain, which keeps its detail in its own table.
    public IReadOnlyDictionary<string, string?> IntakeValues { get; init; } =
        new Dictionary<string, string?>();

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

    // What the request is, from the module that raised it. Null where that module does
    // not supply them — the client falls back to humanising the document type, and a
    // stand-in invented here would be a second vocabulary competing with theirs.
    public string? Title { get; init; }

    public string? Summary { get; init; }

    // The fields filled in on the intake form, for a request raised that way. Empty for
    // every module-driven chain, which keeps its detail in its own table.
    public required IReadOnlyDictionary<string, string?> Fields { get; init; }

    // The schema those fields were filled in against, so the detail page can label and
    // order them without holding its own copy — and so a field map written last year
    // still renders under the labels it was collected with.
    public required IReadOnlyList<IntakeField> FieldSchema { get; init; }

    // Who raised it. The account, the employee behind that account where there is one,
    // and the name — a register with a row per submission and no column for the person
    // who made it is the one thing the screen cannot work without.
    public Guid? SubmittedByUserId { get; init; }

    public Guid? SubmittedByEmployeeId { get; init; }

    public string? SubmittedByName { get; init; }

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

    // The reviewer's signature, where the chain asked for one. Signed on read like
    // every other file link here, so there is nothing stored that can go stale.
    public Guid? SignatureFileId { get; init; }

    public string? SignatureUrl { get; init; }

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
