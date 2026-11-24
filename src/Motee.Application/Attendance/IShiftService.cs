using Motee.Domain.Attendance;

namespace Motee.Application.Attendance;

public enum ShiftOutcome
{
    Succeeded,
    NotFound,
    Refused,
}

// The roster: who is on which work schedule on which day.
//
// The patterns themselves are work schedules and are served by
// WorkSchedulesController. There is no shift-template endpoint here, and that
// is the point — a second list of shift patterns would be a second answer to
// what hours a shift runs, and attendance judges lateness against the work
// schedule.
public interface IShiftService
{
    // The grid: every employee down the side, every day across the top, with
    // the gaps left as gaps.
    Task<RosterResult> RosterAsync(
        RosterQuery query,
        CancellationToken cancellationToken = default);

    // The caller's own shifts, for the employee screen.
    Task<RosterResult> MineAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    Task<ShiftResult> AssignAsync(
        ShiftAssignmentRequest request,
        CancellationToken cancellationToken = default);

    // Called off, which is a decision rather than a deletion: the rota still
    // shows somebody was meant to be there, and a cancelled shift is not the
    // same as a day nobody rostered.
    Task<ShiftResult> CancelAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ShiftOutcome> ClearAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed record RosterResult
{
    public required DateOnly From { get; init; }

    public required DateOnly To { get; init; }

    public required IReadOnlyList<RosterRow> Rows { get; init; }

    public ShiftRefusal Refusal { get; init; }

    public bool Succeeded => Refusal == ShiftRefusal.None;

    // Days across the whole grid with nobody rostered. The figure a manager
    // checking cover actually needs, and the one the client's invented
    // defaults hide by filling every empty square.
    public int UnrosteredDays => Rows.Sum(row => row.UnrosteredDays);

    public static RosterResult Refused(DateOnly from, DateOnly to, ShiftRefusal refusal) =>
        new() { From = from, To = to, Rows = [], Refusal = refusal };
}

public sealed record ShiftResult
{
    public required ShiftOutcome Outcome { get; init; }

    public ShiftAssignmentDto? Assignment { get; init; }

    public ShiftRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == ShiftOutcome.Succeeded;

    public static ShiftResult Ok(ShiftAssignmentDto assignment) =>
        new() { Outcome = ShiftOutcome.Succeeded, Assignment = assignment };

    public static ShiftResult Failed(ShiftOutcome outcome) => new() { Outcome = outcome };

    public static ShiftResult Refused(ShiftRefusal refusal) =>
        new() { Outcome = ShiftOutcome.Refused, Refusal = refusal };
}

public sealed record ShiftAssignmentDto
{
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public required Guid WorkScheduleId { get; init; }

    public required string ShiftName { get; init; }

    public required DateOnly Date { get; init; }

    public TimeOnly? StartTime { get; init; }

    public TimeOnly? EndTime { get; init; }

    public required int BreakMinutes { get; init; }

    public required decimal PaidHours { get; init; }

    public required ShiftState State { get; init; }

    public DateOnly? CancelledOn { get; init; }

    public string? Note { get; init; }
}

public sealed record RosterQuery
{
    public required DateOnly From { get; init; }

    public required DateOnly To { get; init; }

    public Guid? DepartmentId { get; init; }

    public Guid? BranchId { get; init; }

    public Guid? EmployeeId { get; init; }

    // Only the people with a gap somewhere in the range. What somebody
    // checking cover opens the screen to find.
    public bool? WithGaps { get; init; }
}

public sealed record ShiftAssignmentRequest
{
    public required Guid EmployeeId { get; init; }

    public required Guid WorkScheduleId { get; init; }

    public required DateOnly Date { get; init; }

    public string? Note { get; init; }
}
