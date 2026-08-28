using Motee.Domain.Common;

namespace Motee.Domain.Approvals;

// The reusable definition: "an onboarding goes to the line manager, then to the
// department head". Owned by the tenant, because who signs off what is a company's own
// arrangement rather than something the product decides for them.
public class ApprovalTemplate : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // Which category this chain approves — "onboarding", "leave_request". A string so a
    // tenant can add a category we never shipped.
    public required string DocumentType { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    // Used when a module starts an approval without naming a template. One default per
    // document type, enforced on write.
    public bool IsDefault { get; set; }

    // Seeded by us versus built by the tenant. System templates can be copied and
    // deactivated but not edited, so a company cannot quietly change the meaning of one
    // and leave older instances referring to something that no longer matches.
    public bool IsSystem { get; set; }

    public bool IsActive { get; set; } = true;

    public required DateTimeOffset CreatedAt { get; set; }

    public required DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}

public class ApprovalTemplateStep : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid TemplateId { get; set; }

    // The position in the chain, contiguous from zero. Rewritten on save rather than
    // trusted from the client: gaps and duplicates make "the next step" ambiguous.
    //
    // Named Sequence rather than Order because "order" is reserved in SQL — a column
    // called that has to be quoted in every statement that touches it, for ever.
    public required int Sequence { get; set; }

    // The unit of work, as the approver sees it: "Verify right to work".
    public required string Label { get; set; }

    public required ApproverResolver Approver { get; set; }

    // A step that cannot be resolved to a person is skipped rather than blocking, when
    // it is optional. A required one stops the chain and says why.
    public bool Required { get; set; } = true;
}

// One run of a template against one thing — this employee's onboarding, that leave
// request.
//
// The template is snapshotted into the steps below rather than referenced live. An admin
// editing a template must not change an approval already in flight: an approval that
// grows a step halfway through is one nobody can account for afterwards.
public class ApprovalInstance : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid TemplateId { get; set; }

    public required string DocumentType { get; set; }

    // What is being approved, by the module that started it. Deliberately loose: the
    // engine does not know what an onboarding record is, and should not have to.
    public required string SubjectType { get; set; }

    public required Guid SubjectId { get; set; }

    public required ApprovalStatus Status { get; set; }

    // Whose approval this is. Usually an employee, so the resolvers have somewhere to
    // start: the line manager of whom, the head of which department.
    public Guid? SubjectEmployeeId { get; set; }

    public Guid? SubmittedByUserId { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    // How many times it has been through. A document returned three times is a fact
    // worth having on the row rather than counted from the event log every time.
    public int Round { get; set; } = 1;

    public required DateTimeOffset CreatedAt { get; set; }

    public required DateTimeOffset UpdatedAt { get; set; }
}

public class ApprovalStepInstance : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid InstanceId { get; set; }

    // Kept so a decision can still be traced to the template step it came from, even
    // after the template has moved on.
    public Guid TemplateStepId { get; set; }

    public required int Sequence { get; set; }

    // Copied from the template at submission, not read through. This is the snapshot:
    // the label and the rule as they were when the person was asked.
    public required string Label { get; set; }

    public required ApproverResolver Approver { get; set; }

    public bool Required { get; set; }

    // Who it actually landed on, worked out at submission. Null when nobody could be
    // found — an employee with no manager recorded, a department with no head.
    public Guid? ResolvedEmployeeId { get; set; }

    public Guid? ResolvedUserId { get; set; }

    public required ApprovalStepStatus Status { get; set; }

    public Guid? DecidedByUserId { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public string? Note { get; set; }

    // Why it was passed over, when it was. "on_leave" arrives with phase 3;
    // "unresolved" is the phase 1 case of nobody to ask.
    public string? SkippedReason { get; set; }
}

// Everything that happened, in order. The instance says where it got to; this says how
// it got there, which is the half an auditor asks for.
public class ApprovalEvent : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid InstanceId { get; set; }

    public required string Type { get; set; }

    public int? StepOrder { get; set; }

    public Guid? ActorUserId { get; set; }

    // Copied, not joined — a deleted or renamed user must not erase who approved
    // something two years ago.
    public string? ActorName { get; set; }

    public string? Note { get; set; }

    public required DateTimeOffset At { get; set; }
}

public static class ApprovalEventTypes
{
    public const string Submitted = "submitted";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Returned = "returned";
    public const string Resubmitted = "resubmitted";
    public const string Cancelled = "cancelled";
    public const string Skipped = "skipped";
}
