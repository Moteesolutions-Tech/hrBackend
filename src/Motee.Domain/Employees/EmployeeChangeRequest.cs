using Motee.Domain.Common;

namespace Motee.Domain.Employees;

// An employee asking for one of their own details to be corrected.
//
// The thing this replaces is worth recording. The profile screen kept change requests
// in a JSON file on the web server — one file, no tenant, no account behind it,
// whole-snapshot writes — and applied an approved change as a client-side "override"
// laid over the employee it had loaded. So a request never reached the employee record
// at all: HR approved it, the browser remembered, and the next person to open the
// profile saw the old value. Two people approving at once lost one of the decisions.
//
// Here the approval writes the field, through the ProfileFields whitelist, and the
// override map stops being needed.
public class EmployeeChangeRequest : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // Whose record it is about. Always the requester's own — an employee cannot raise
    // one against somebody else, which the service enforces rather than this type.
    public Guid EmployeeId { get; set; }

    // A key from ProfileFields.Catalogue. Stored as the key rather than a label, so a
    // change to the wording does not orphan the history.
    public required string Field { get; set; }

    public string? RequestedValue { get; set; }

    // What the field held when the request was raised.
    //
    // A snapshot, and deliberately not re-read at approval: it is the record of what the
    // employee was looking at when they said "this is wrong". If HR corrects the field
    // another way in the meantime, the approval still applies the requested value — the
    // request means "make it this", not "change it from that" — and the snapshot shows
    // the reviewer what the employee saw.
    public string? CurrentValue { get; set; }

    public required string Reason { get; set; }

    // Evidence, where the change needs it: a deed poll for a name, a bill for an
    // address. Referenced rather than posted as bytes, like every other attachment.
    public Guid? FileId { get; set; }

    public ChangeRequestStatus Status { get; set; } = ChangeRequestStatus.Pending;

    // The account that raised it, not the employee id. The employee is on the row
    // already, and this answers "who was signed in" — which is the auditable fact.
    public Guid? RequestedByUserId { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public Guid? DecidedByUserId { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    // Why it was refused, or a note on the approval. Required on a rejection: being
    // told no without being told why sends somebody to HR to ask, which is the cost
    // the field exists to avoid.
    public string? DecisionNote { get; set; }
}

public enum ChangeRequestStatus
{
    Pending,
    Approved,
    Rejected,

    // Taken back by the employee before anybody decided. Distinct from rejected: the
    // log should not read as though HR refused something nobody ever ruled on.
    Withdrawn,
}

public static class ChangeRequests
{
    public const int LongestReason = 1000;

    public const int LongestNote = 1000;

    // A decision can only be made on something still waiting for one. Without this,
    // approving twice applies the change twice — harmless for a name, not harmless for
    // the audit trail, which would record two different people deciding the same thing.
    public static bool IsOpen(ChangeRequestStatus status) =>
        status == ChangeRequestStatus.Pending;
}
