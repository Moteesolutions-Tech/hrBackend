using Motee.Application.Common;
using Motee.Domain.Attendance;

namespace Motee.Application.Attendance;

// Weeks, handed in and signed off.
//
// A timesheet is a lifecycle wrapped around days that already exist — it adds a
// submission and an approval, not a second record of the hours. Everything numeric
// here is derived from the attendance rows, so a corrected day changes the week that
// contains it rather than leaving the two disagreeing.
public interface ITimesheetService
{
    // The caller's own week. Always returns one: a week nobody has handed in is
    // Pending rather than absent, because the screen draws it either way.
    Task<TimesheetDto?> MineAsync(
        DateOnly? weekStart = null,
        CancellationToken cancellationToken = default);

    Task<TimesheetResult> SubmitAsync(
        DateOnly weekStart,
        CancellationToken cancellationToken = default);

    // ── reviewing ───────────────────────────────────────────────────────────

    Task<PagedResult<TimesheetDto>> ListAsync(
        TimesheetQuery query,
        CancellationToken cancellationToken = default);

    Task<TimesheetDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<TimesheetResult> ApproveAsync(Guid id, CancellationToken cancellationToken = default);

    Task<TimesheetResult> RejectAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default);
}

public sealed record TimesheetDto
{
    // Empty until the week has been handed in once. A week nobody has touched has no
    // row behind it, and inventing one on every read would write a record for every
    // employee for every week they never worked.
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? DepartmentName { get; init; }

    public required DateOnly WeekStart { get; init; }

    public required DateOnly WeekEnd { get; init; }

    public required TimesheetStatus Status { get; init; }

    public required decimal TotalHours { get; init; }

    public required decimal OvertimeHours { get; init; }

    public required int DaysPresent { get; init; }

    public required int DaysLate { get; init; }

    public required int DaysAbsent { get; init; }

    // The days themselves, so the week can be opened without a second call.
    public required IReadOnlyList<AttendanceDayDto> Days { get; init; }

    public DateTimeOffset? SubmittedAt { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    public string? ResolvedByName { get; init; }

    public string? RejectionReason { get; init; }

    // What the figures were when somebody approved them. Null until approved.
    public decimal? ApprovedTotalHours { get; init; }

    public decimal? ApprovedOvertimeHours { get; init; }

    // True when a day inside an approved week has been corrected since, so the hours
    // no longer match what was signed off.
    //
    // Surfaced rather than hidden by overwriting the snapshot: somebody approved a
    // number, and a week whose total has moved underneath that approval is exactly
    // the thing payroll needs to be told about rather than quietly handed.
    public required bool ChangedSinceApproval { get; init; }
}

public sealed record TimesheetQuery : PagedQuery
{
    public DateOnly? WeekStart { get; init; }

    public Guid? EmployeeId { get; init; }

    public Guid? DepartmentId { get; init; }

    public TimesheetStatus? Status { get; init; }
}

public enum TimesheetOutcome
{
    Succeeded,
    NotFound,
    EmployeeNotFound,

    // The lifecycle will not make that move. The message says which.
    NotAllowed,

    // A week that has not finished yet. Handing in Thursday's week on Thursday means
    // approving days that have not happened.
    WeekNotOver,

    // Rejecting without saying why. The person has to guess, and will hand the same
    // week back.
    ReasonRequired,
}

public sealed record TimesheetResult
{
    public required TimesheetOutcome Outcome { get; init; }

    public TimesheetDto? Timesheet { get; init; }

    public string? Reason { get; init; }

    public bool Succeeded => Outcome == TimesheetOutcome.Succeeded;

    public static TimesheetResult Failed(TimesheetOutcome outcome, string? reason = null) =>
        new() { Outcome = outcome, Reason = reason };

    public static TimesheetResult Ok(TimesheetDto timesheet) =>
        new() { Outcome = TimesheetOutcome.Succeeded, Timesheet = timesheet };
}
