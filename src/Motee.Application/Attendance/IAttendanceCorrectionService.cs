using Motee.Application.Common;
using Motee.Domain.Attendance;

namespace Motee.Application.Attendance;

// Claims that a recorded day is wrong.
//
// The employee asks; somebody else decides. Approving writes the requested times onto
// the day and keeps what the clock originally said, so a corrected record never
// passes for a punch.
public interface IAttendanceCorrectionService
{
    Task<IReadOnlyList<AttendanceCorrectionDto>> MineAsync(
        CancellationToken cancellationToken = default);

    Task<CorrectionResult> RequestAsync(
        RequestCorrectionRequest request,
        CancellationToken cancellationToken = default);

    Task<PagedResult<AttendanceCorrectionDto>> ListAsync(
        CorrectionQuery query,
        CancellationToken cancellationToken = default);

    Task<CorrectionResult> ApproveAsync(
        Guid id,
        string? note = null,
        CancellationToken cancellationToken = default);

    Task<CorrectionResult> RejectAsync(
        Guid id,
        string note,
        CancellationToken cancellationToken = default);
}

public sealed record AttendanceCorrectionDto
{
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? DepartmentName { get; init; }

    public required DateOnly Date { get; init; }

    public DateTimeOffset? RequestedClockInAt { get; init; }

    public DateTimeOffset? RequestedClockOutAt { get; init; }

    public required string Reason { get; init; }

    public required CorrectionStatus Status { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    public string? ResolvedByName { get; init; }

    public string? ResolutionNote { get; init; }

    // What the clock said before this was applied. The pair that stops a corrected
    // day passing for a punched one.
    public DateTimeOffset? OriginalClockInAt { get; init; }

    public DateTimeOffset? OriginalClockOutAt { get; init; }
}

public sealed record RequestCorrectionRequest
{
    public required DateOnly Date { get; init; }

    public DateTimeOffset? ClockInAt { get; init; }

    public DateTimeOffset? ClockOutAt { get; init; }

    public required string Reason { get; init; }
}

public sealed record CorrectionQuery : PagedQuery
{
    public Guid? EmployeeId { get; init; }

    public CorrectionStatus? Status { get; init; }
}

public enum CorrectionOutcome
{
    Succeeded,
    NotFound,
    EmployeeNotFound,

    // Already approved or already turned down.
    NotAllowed,

    ReasonRequired,

    // Asking to change nothing. The frontend blocks it, and so does this — an empty
    // request would sit in somebody's queue meaning nothing.
    NothingToChange,

    // A day that has not happened. There is nothing to correct about tomorrow.
    InTheFuture,

    OutOfOrder,

    // One open claim per day. Two would be two answers to the same question, and
    // approving both would apply whichever was read last.
    AlreadyPending,
}

public sealed record CorrectionResult
{
    public required CorrectionOutcome Outcome { get; init; }

    public AttendanceCorrectionDto? Correction { get; init; }

    public string? Reason { get; init; }

    public bool Succeeded => Outcome == CorrectionOutcome.Succeeded;

    public static CorrectionResult Failed(CorrectionOutcome outcome, string? reason = null) =>
        new() { Outcome = outcome, Reason = reason };

    public static CorrectionResult Ok(AttendanceCorrectionDto correction) =>
        new() { Outcome = CorrectionOutcome.Succeeded, Correction = correction };
}
