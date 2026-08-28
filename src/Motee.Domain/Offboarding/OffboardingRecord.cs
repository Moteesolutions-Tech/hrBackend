using Motee.Domain.Common;

namespace Motee.Domain.Offboarding;

// One person leaving, and everything that had to happen because of it.
//
// Separate from the employee row rather than a set of columns on it: an exit has its own
// decisions, its own approvals and its own history, and a withdrawn one has to leave a
// record behind. Columns on the employee would be overwritten the next time somebody
// resigned, which is exactly when you want the first one still readable.
public class OffboardingRecord : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public required OffboardingStatus Status { get; set; }

    public required ExitReason ExitReason { get; set; }

    // The date they actually stop working, which is not the date the record was made and
    // not the date it completes. Payroll and access both key off this one.
    public required DateOnly LastWorkingDate { get; set; }

    public string? Notes { get; set; }

    public Guid? InitiatedByUserId { get; set; }

    public required DateTimeOffset InitiatedAt { get; set; }

    // Who decided, and when. Kept per decision rather than as one "changed by", because
    // the person who approves an exit and the person who later withdraws it are
    // routinely different, and both need to be answerable for their own act.
    public Guid? DecidedByUserId { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    // Required when disapproving. An exit refused without a reason is a decision nobody
    // can defend later.
    public string? DecisionReason { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? ReactivatedAt { get; set; }

    public Guid? ReactivatedByUserId { get; set; }

    // The moment their sessions were revoked. Distinct from the clearance item of the
    // same name: one is a tick a person makes, this is when the system actually did it.
    public DateTimeOffset? SystemAccessRevokedAt { get; set; }

    // Scheduling and holding an exit interview are different events, and HR chase the
    // gap between them.
    public DateTimeOffset? ExitInterviewScheduledAt { get; set; }

    public DateTimeOffset? ExitInterviewCompletedAt { get; set; }

    public string? ExitInterviewNotes { get; set; }

    public DateTimeOffset? ExitDocumentsGeneratedAt { get; set; }

    public required DateTimeOffset UpdatedAt { get; set; }
}

// One thing that has to be handed back, signed off or done before someone goes.
//
// A row per item rather than a JSON blob on the record: each carries who completed it
// and when, and "who signed off the laptop return" is a question asked months later,
// after the person who did it has themselves left.
public class OffboardingClearanceItem : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid OffboardingRecordId { get; set; }

    public required string Label { get; set; }

    // Which team owns it — HR, IT, Finance, Manager, Employee. A string rather than an
    // enum because tenants add their own, and a fixed list would need a release to
    // accommodate a company that has a Fleet team.
    public required string Department { get; set; }

    // Ordering as presented, so a checklist reads in the sequence it should be worked.
    public required int Sequence { get; set; }

    public bool Completed { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public Guid? CompletedByUserId { get; set; }

    public string? Notes { get; set; }
}

// The list every leaver starts with. Tenant-editable later; fixed now, because a
// configurable checklist nobody has configured is an empty one.
public static class DefaultClearance
{
    public static readonly IReadOnlyList<(string Label, string Department)> Items =
    [
        ("Acknowledge resignation", "HR"),
        ("Handover document", "Employee"),
        ("Knowledge transfer meeting", "Manager"),
        ("Return company laptop & badge", "Employee"),
        ("Revoke system access", "IT"),
        ("Exit interview", "HR"),
        ("Final-pay processed", "Finance"),
    ];
}
