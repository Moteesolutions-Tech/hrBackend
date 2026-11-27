using Motee.Application.Common;
using Motee.Domain.Employees;

namespace Motee.Application.Employees;

// Employees asking for their own details to be corrected, and HR deciding.
//
// Separate from IEmployeeService, which owns the record. This owns the asking: who may
// raise one, which fields may be named, and what approving actually does — which is to
// write the field through the ProfileFields whitelist rather than to remember a decision
// somewhere a later reader has to know to consult.
public interface IChangeRequestService
{
    // The fields an employee may ask about, with their labels and current values for
    // that employee. Served rather than hardcoded in the client, because a field the
    // form offers that the backend will refuse is a request somebody cannot finish.
    Task<IReadOnlyList<RequestableFieldDto>> FieldsAsync(
        CancellationToken cancellationToken = default);

    // The signed-in employee asking about their own record. There is no parameter for
    // whose record: an employee cannot raise one against somebody else, and leaving the
    // subject out of the signature is what makes that impossible rather than checked.
    Task<ChangeRequestResult> RaiseAsync(
        RaiseChangeRequest request,
        CancellationToken cancellationToken = default);

    // Taken back before anybody decided. Theirs to withdraw, so the service checks the
    // caller raised it.
    Task<ChangeRequestResult> WithdrawAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    // The caller's own log, newest first. No parameter for whose, for the same reason
    // RaiseAsync has none.
    Task<IReadOnlyList<ChangeRequestDto>> MineAsync(
        CancellationToken cancellationToken = default);

    // One employee's log, for HR reading a profile. Reach is checked at the API edge,
    // the same way the profile and guarantors reads are — the module permission says
    // somebody may read an employee, not which one.
    Task<IReadOnlyList<ChangeRequestDto>> ForEmployeeAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default);

    // The HR queue. Pending by default, because that is the only status anybody has to
    // act on.
    Task<PagedResult<ChangeRequestDto>> ListAsync(
        ChangeRequestQuery query,
        CancellationToken cancellationToken = default);

    // Applies the change to the employee record, then marks the request decided. In one
    // transaction: a request recorded as approved whose field never changed is worse
    // than a failure, because nothing afterwards will notice.
    Task<ChangeRequestResult> ApproveAsync(
        Guid id,
        string? note = null,
        CancellationToken cancellationToken = default);

    Task<ChangeRequestResult> RejectAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default);
}

public enum ChangeRequestOutcome
{
    Succeeded,
    NotFound,

    // The signed-in account has no employee record, so it cannot raise one. The owner
    // is permanently in this state.
    NoEmployeeRecord,

    // Not the person who raised it.
    NotTheRequester,

    // Already decided. A second decision would record two people ruling on one thing.
    AlreadyDecided,

    // A field outside ProfileFields.Catalogue. Covers both a typo and an attempt to
    // name something nobody may request, and says the same thing to each — the catalogue
    // is public, so there is nothing to learn from a sharper message.
    FieldNotRequestable,

    // Already a pending request on that field for that employee. Two would leave the
    // queue with a decision to make twice and the second silently overwriting the first.
    AlreadyPending,

    // The requested value is the one already on the record.
    NoChange,

    ReasonRequired,

    ReasonTooLong,

    // A rejection with nothing said. Being told no without being told why sends
    // somebody to HR to ask.
    NoteRequired,

    // The file id is not this tenant's, or not a change-request attachment.
    UnknownFile,

    // The value cannot go in the column: too long, not a date, a date that cannot be
    // true, or a required field being cleared. Caught at approval, which is the moment
    // it would otherwise reach the record.
    ValueRefused,
}

public sealed record ChangeRequestQuery : PagedQuery
{
    public ChangeRequestStatus? Status { get; init; } = ChangeRequestStatus.Pending;

    public Guid? EmployeeId { get; init; }
}

public sealed record RaiseChangeRequest
{
    public required string Field { get; init; }

    // Null clears an optional field, which is a legitimate request: a preferred name or
    // a second phone number can stop being true.
    public string? RequestedValue { get; init; }

    public required string Reason { get; init; }

    public Guid? FileId { get; init; }
}

public sealed record RequestableFieldDto
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    // personal, contact, address, emergency — matching the groups the profile screen
    // already draws.
    public required ProfileFieldGroup Group { get; init; }

    // What the field holds for the caller right now, so the form can show it beside the
    // box without a second read per field.
    public string? CurrentValue { get; init; }
}

public sealed record ChangeRequestDto
{
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public required string Field { get; init; }

    // Resolved from the catalogue on the way out, so the log reads "Preferred name"
    // rather than "preferredName" and the wording lives in one place.
    public required string Label { get; init; }

    public string? CurrentValue { get; init; }

    public string? RequestedValue { get; init; }

    public required string Reason { get; init; }

    public Guid? FileId { get; init; }

    // Signed on read, like every other file link here. The bucket is private, so a URL
    // kept in the database would be a broken link with a delay on it.
    public string? FileUrl { get; init; }

    public required ChangeRequestStatus Status { get; init; }

    public string? RequestedByName { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    public string? DecidedByName { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }

    public string? DecisionNote { get; init; }
}

public sealed record ChangeRequestResult
{
    public required ChangeRequestOutcome Outcome { get; init; }

    public ChangeRequestDto? Request { get; init; }

    // Why a value was refused, in words, when the outcome alone cannot say — "Date of
    // birth cannot be in the future" rather than "value refused". Whoever has to fix it
    // needs the sentence, not the category.
    public string? Reason { get; init; }

    public bool Succeeded => Outcome == ChangeRequestOutcome.Succeeded;

    public static ChangeRequestResult Failed(
        ChangeRequestOutcome outcome,
        string? reason = null) =>
        new() { Outcome = outcome, Reason = reason };

    public static ChangeRequestResult Ok(ChangeRequestDto request) =>
        new() { Outcome = ChangeRequestOutcome.Succeeded, Request = request };
}
