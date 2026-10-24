namespace Motee.Application.Attendance;

// The contracted hours a company runs, and who is on which.
//
// Attendance asks this for every judgement it makes, so the one method that matters is
// EffectiveForAsync: it answers "which hours is this person held to", which is their
// own schedule or the company default, and never nothing.
public interface IWorkScheduleService
{
    Task<IReadOnlyList<WorkScheduleDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<WorkScheduleDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<WorkScheduleResult> CreateAsync(
        WorkScheduleRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkScheduleResult> UpdateAsync(
        Guid id,
        WorkScheduleRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkScheduleOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    // Moves the default. Exactly one schedule holds it, so this is a move rather than
    // a flag somebody sets twice.
    Task<WorkScheduleOutcome> MakeDefaultAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<WorkScheduleOutcome> AssignAsync(
        Guid employeeId,
        Guid? scheduleId,
        CancellationToken cancellationToken = default);

    // The hours this person is actually held to: their own if they have one, the
    // company default otherwise.
    //
    // Null only when the employee does not exist, or when a tenant somehow has no
    // default at all — which registration prevents and the unique index keeps to at
    // most one. Callers treat null as "cannot judge this day" rather than guessing.
    Task<WorkScheduleDto?> EffectiveForAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default);
}

public sealed record WorkingHoursDto
{
    // Local times. A shift starts at nine where the person is, and an instant would
    // move it when the clocks change.
    public required TimeOnly Start { get; init; }

    public required TimeOnly End { get; init; }
}

public sealed record WorkScheduleDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    // Keyed by day name — "monday" — so the client reads it without knowing the order
    // of an enum. Days absent from the map are not worked.
    public required IReadOnlyDictionary<string, WorkingHoursDto> Days { get; init; }

    public required int BreakMinutes { get; init; }

    public required int GraceMinutes { get; init; }

    public required bool IsDefault { get; init; }

    public required bool IsActive { get; init; }

    // Derived from the days, never stored beside them. A stored total is a second
    // answer to the same question, and the two disagree the first time one is edited.
    public required decimal WeeklyHours { get; init; }

    // How many people are on it. The number that makes "can I delete this?" a question
    // with a visible answer.
    public required int AssignedCount { get; init; }
}

public sealed record WorkScheduleRequest
{
    public required string Name { get; init; }

    public required IReadOnlyDictionary<string, WorkingHoursDto> Days { get; init; }

    public int BreakMinutes { get; init; }

    public int GraceMinutes { get; init; } = 10;
}

public enum WorkScheduleOutcome
{
    Succeeded,
    NotFound,
    DuplicateName,

    // Every schedule needs at least one working day. A pattern with none is not a
    // part-time week, it is a person contracted to never work.
    NoWorkingDays,

    // End is not after start, on some day. Allowed to wrap midnight for a night
    // shift; not allowed to be the same instant, which is a zero-hour day.
    InvalidHours,

    // A break longer than the shift it is taken from leaves negative paid hours.
    BreakLongerThanShift,

    // People are on it. Reassign them first — deleting it would silently move them
    // onto the default and change what counts as late for them.
    InUse,

    // The default cannot be deleted, and cannot be deactivated. Something has to
    // catch a new hire, and a company that has deleted its only schedule discovers
    // it one punch at a time.
    IsDefault,

    EmployeeNotFound,
}

public sealed record WorkScheduleResult
{
    public required WorkScheduleOutcome Outcome { get; init; }

    public WorkScheduleDto? Schedule { get; init; }

    public bool Succeeded => Outcome == WorkScheduleOutcome.Succeeded;

    public static WorkScheduleResult Failed(WorkScheduleOutcome outcome) =>
        new() { Outcome = outcome };

    public static WorkScheduleResult Ok(WorkScheduleDto schedule) =>
        new() { Outcome = WorkScheduleOutcome.Succeeded, Schedule = schedule };
}
