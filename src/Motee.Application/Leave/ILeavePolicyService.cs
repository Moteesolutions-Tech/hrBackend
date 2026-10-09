using Motee.Domain.Leave;

namespace Motee.Application.Leave;

// The rules HR configure: what kinds of leave exist, what each is worth, and which days
// the company is closed.
//
// Changed rarely and read constantly, which is why editing one must never reach a request
// already booked — every request snapshots its own day count for exactly that reason.
public interface ILeavePolicyService
{
    Task<IReadOnlyList<LeaveTypeDto>> ListTypesAsync(CancellationToken cancellationToken = default);

    Task<LeaveTypeResult> CreateTypeAsync(
        LeaveTypeRequest request,
        CancellationToken cancellationToken = default);

    Task<LeaveTypeResult> UpdateTypeAsync(
        Guid id,
        LeaveTypeRequest request,
        CancellationToken cancellationToken = default);

    // Deactivation, not deletion. Requests point at a type and history has to keep
    // reading correctly, so retiring one hides it from the picker and leaves the past
    // intact.
    Task<LeavePolicyOutcome> DeactivateTypeAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PublicHolidayDto>> ListHolidaysAsync(
        int? year = null,
        CancellationToken cancellationToken = default);

    Task<PublicHolidayResult> AddHolidayAsync(
        PublicHolidayRequest request,
        CancellationToken cancellationToken = default);

    Task<LeavePolicyOutcome> RemoveHolidayAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    // Periods the company will not approve planned leave over — the Christmas trading
    // run, year-end close. Sits beside public holidays in settings and is the opposite of
    // one: a holiday is a day nobody works, a blackout is a day everybody does.
    Task<IReadOnlyList<LeaveBlackoutDto>> ListBlackoutsAsync(
        CancellationToken cancellationToken = default);

    Task<LeaveBlackoutResult> SaveBlackoutAsync(
        Guid? id,
        LeaveBlackoutRequest request,
        CancellationToken cancellationToken = default);

    Task<LeavePolicyOutcome> RemoveBlackoutAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    // Fills in a year from the built-in calendar. The computed days only — Eid and the
    // other moon-sighting holidays are announced rather than calculable, so they stay a
    // company's own entry.
    Task<IReadOnlyList<PublicHolidayDto>> GenerateHolidaysAsync(
        int year,
        CancellationToken cancellationToken = default);
}

public enum LeavePolicyOutcome
{
    Succeeded,
    NotFound,
    DuplicateName,

    // A holiday already recorded on that date.
    DuplicateDate,

    // Days per year below zero, or a carry-over cap above the entitlement.
    InvalidRule,
}

public sealed record LeaveTypeRequest
{
    public required string Name { get; init; }

    public bool IsPaid { get; init; } = true;

    public bool IsActive { get; init; } = true;

    public required LeavePolicyRequest Policy { get; init; }
}

public sealed record LeavePolicyRequest
{
    public string? Description { get; init; }

    public required decimal DaysPerYear { get; init; }

    public int MinNoticeDays { get; init; }

    public int MaxConsecutiveDays { get; init; }

    public bool ExcludePublicHolidays { get; init; } = true;

    public bool RequiresMedicalCertificate { get; init; }

    public string? AttachmentRequirement { get; init; }

    // A bank of days somebody runs down across the year, or a ceiling for one occasion.
    // Annual and sick leave are banks; maternity and compassionate leave are not.
    public bool TracksBalance { get; init; } = true;

    public bool CarryOverAllowed { get; init; }

    public decimal MaxCarryOverDays { get; init; }

    public int CarryOverExpiryMonths { get; init; }

    public bool AccruesMonthly { get; init; }

    public string? Eligibility { get; init; }

    public string? PublicHolidayNote { get; init; }

    public string? DocumentUrl { get; init; }
}

public sealed record LeaveBlackoutRequest
{
    public required string Name { get; init; }

    public string? Reason { get; init; }

    public required DateOnly StartDate { get; init; }

    public required DateOnly EndDate { get; init; }

    // At least one, always. There is deliberately no "everything" option: sick leave is
    // reported rather than requested, and a blackout covering it would tell an ill person
    // they may not be ill until January.
    public required IReadOnlyList<Guid> LeaveTypeIds { get; init; }

    // Empty means the whole company.
    public IReadOnlyList<Guid> DepartmentIds { get; init; } = [];

    public bool IsActive { get; init; } = true;
}

public sealed record LeaveBlackoutDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Reason { get; init; }

    public required DateOnly StartDate { get; init; }

    public required DateOnly EndDate { get; init; }

    public required IReadOnlyList<Guid> LeaveTypeIds { get; init; }

    // Resolved for display, so the screen does not have to join the type list itself.
    public required IReadOnlyList<string> LeaveTypeNames { get; init; }

    public required IReadOnlyList<Guid> DepartmentIds { get; init; }

    public required IReadOnlyList<string> DepartmentNames { get; init; }

    public required bool IsActive { get; init; }

    // Whole company when no departments are named. Stated rather than left to the client
    // to infer from an empty list, which is the kind of inference that gets inverted.
    public bool AppliesToEveryone => DepartmentIds.Count == 0;
}

public sealed record LeaveBlackoutResult
{
    public required LeavePolicyOutcome Outcome { get; init; }

    public LeaveBlackoutDto? Blackout { get; init; }

    public bool Succeeded => Outcome == LeavePolicyOutcome.Succeeded;

    public static LeaveBlackoutResult Failed(LeavePolicyOutcome outcome) => new() { Outcome = outcome };

    public static LeaveBlackoutResult Ok(LeaveBlackoutDto blackout) =>
        new() { Outcome = LeavePolicyOutcome.Succeeded, Blackout = blackout };
}

public sealed record PublicHolidayRequest
{
    public required DateOnly Date { get; init; }

    public required string Name { get; init; }

    public string? CountryCode { get; init; }
}

public sealed record LeaveTypeResult
{
    public required LeavePolicyOutcome Outcome { get; init; }

    public LeaveTypeDto? Type { get; init; }

    public bool Succeeded => Outcome == LeavePolicyOutcome.Succeeded;

    public static LeaveTypeResult Failed(LeavePolicyOutcome outcome) => new() { Outcome = outcome };

    public static LeaveTypeResult Ok(LeaveTypeDto type) =>
        new() { Outcome = LeavePolicyOutcome.Succeeded, Type = type };
}

public sealed record PublicHolidayResult
{
    public required LeavePolicyOutcome Outcome { get; init; }

    public PublicHolidayDto? Holiday { get; init; }

    public bool Succeeded => Outcome == LeavePolicyOutcome.Succeeded;

    public static PublicHolidayResult Failed(LeavePolicyOutcome outcome) =>
        new() { Outcome = outcome };

    public static PublicHolidayResult Ok(PublicHolidayDto holiday) =>
        new() { Outcome = LeavePolicyOutcome.Succeeded, Holiday = holiday };
}

public sealed record LeaveTypeDto
{
    public required Guid Id { get; init; }

    public string? Code { get; init; }

    public required string Name { get; init; }

    public required bool IsPaid { get; init; }

    public required bool IsActive { get; init; }

    public required int Sequence { get; init; }

    // Null only if a type somehow lost its policy, which would leave nobody able to book
    // it — surfaced rather than hidden so the screen can say so.
    public LeavePolicyDto? Policy { get; init; }

    // Live requests against it. The screen warns before a change that would affect
    // people mid-booking.
    public required int OpenRequests { get; init; }
}

public sealed record LeavePolicyDto
{
    public required Guid Id { get; init; }

    public string? Description { get; init; }

    public required decimal DaysPerYear { get; init; }

    public required int MinNoticeDays { get; init; }

    public required int MaxConsecutiveDays { get; init; }

    public required bool ExcludePublicHolidays { get; init; }

    public required bool RequiresMedicalCertificate { get; init; }

    public string? AttachmentRequirement { get; init; }

    public required bool TracksBalance { get; init; }

    public required bool CarryOverAllowed { get; init; }

    public required decimal MaxCarryOverDays { get; init; }

    public required int CarryOverExpiryMonths { get; init; }

    public required bool AccruesMonthly { get; init; }

    public string? Eligibility { get; init; }

    public string? PublicHolidayNote { get; init; }

    public string? DocumentUrl { get; init; }
}

public sealed record PublicHolidayDto
{
    public required Guid Id { get; init; }

    public required DateOnly Date { get; init; }

    public required string Name { get; init; }

    public string? CountryCode { get; init; }
}
