using Motee.Domain.Calendar;

namespace Motee.Application.Calendar;

// The calendar: one window, every source that has something to say about it.
//
// Almost nothing here is a row. Leave, cover, public holidays, birthdays, work
// anniversaries, interviews and review due dates are read from the records that
// already hold them and shaped into items on the way out. Copying them into a
// calendar table would create a second answer to "is Bola off on Thursday", and
// the two would part company the first time a request was cancelled.
//
// What is stored is the handful of things somebody typed: a company-wide event,
// or an event put on named people's schedules.
public interface ICalendarService
{
    // Everything visible to the caller in a window.
    //
    // EmployeeIds says whose schedules to include and defaults to the caller's
    // own. Company events, public holidays, birthdays and work anniversaries
    // come back regardless — they belong to everybody, and leaving them out
    // when somebody views a colleague's week would make the week look empty.
    Task<IReadOnlyList<CalendarItemDto>> AgendaAsync(
        CalendarQuery query,
        CancellationToken cancellationToken = default);

    // The action centre's "next seven days", which is the agenda over a short
    // window with nothing else asked of it.
    Task<IReadOnlyList<CalendarItemDto>> UpcomingAsync(
        int days = 7,
        CancellationToken cancellationToken = default);

    Task<CalendarEventDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // canActForAnyone is resolved at the API edge from workspace.calendar, the
    // same way the employee form resolves its medical block. Without it a person
    // may still add events to their own calendar and to their direct reports' —
    // that is self-service and line management, not a grant.
    Task<CalendarEventResult> CreateAsync(
        CalendarEventRequest request,
        bool canActForAnyone = false,
        CancellationToken cancellationToken = default);

    Task<CalendarEventResult> UpdateAsync(
        Guid id,
        CalendarEventRequest request,
        bool canActForAnyone = false,
        CancellationToken cancellationToken = default);

    Task<CalendarEventOutcome> DeleteAsync(
        Guid id,
        bool canActForAnyone = false,
        CancellationToken cancellationToken = default);
}

// One thing on one day, whatever produced it.
public sealed record CalendarItemDto
{
    // Stable across reads and unique across sources, because the grid keys on
    // it: "event:<guid>", "leave:<guid>", "birthday:<guid>:2026-06-15". A
    // recurring date carries the occurrence, or a year view holding the same
    // birthday twice would render one of them.
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required DateOnly Date { get; init; }

    // Inclusive last day. Null for a single-day item.
    public DateOnly? EndDate { get; init; }

    public TimeOnly? StartTime { get; init; }

    public TimeOnly? EndTime { get; init; }

    public required bool AllDay { get; init; }

    public required CalendarCategory Category { get; init; }

    public required CalendarLayer Layer { get; init; }

    public string? Description { get; init; }

    // Whose schedule it sits on. Null for company-wide items.
    public Guid? PersonId { get; init; }

    public string? PersonName { get; init; }

    // Asked for but not yet approved. The grid draws these dashed, because a
    // fortnight somebody has requested is not a fortnight they are having.
    public bool Pending { get; init; }

    // Set only for stored events — the rest have no row to edit.
    public Guid? EventId { get; init; }

    // Whether this caller may remove it, so the detail panel knows whether to
    // offer a button that would then fail.
    public bool CanDelete { get; init; }
}

public sealed record CalendarQuery
{
    public required DateOnly From { get; init; }

    public required DateOnly To { get; init; }

    // Empty means the caller's own schedule.
    public IReadOnlyList<Guid> EmployeeIds { get; init; } = [];
}

public sealed record CalendarEventDto
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public required DateOnly Date { get; init; }

    public DateOnly? EndDate { get; init; }

    public required bool AllDay { get; init; }

    public TimeOnly? StartTime { get; init; }

    public TimeOnly? EndTime { get; init; }

    public required CalendarCategory Category { get; init; }

    public required CalendarAudience Audience { get; init; }

    public Guid? CreatedByEmployeeId { get; init; }

    public string? CreatedByName { get; init; }

    public required IReadOnlyList<CalendarAttendeeDto> Attendees { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record CalendarAttendeeDto
{
    public required Guid EmployeeId { get; init; }

    public required string Name { get; init; }
}

public sealed record CalendarEventRequest
{
    public required string Title { get; init; }

    public string? Description { get; init; }

    public required DateOnly Date { get; init; }

    public DateOnly? EndDate { get; init; }

    public bool AllDay { get; init; }

    public TimeOnly? StartTime { get; init; }

    public TimeOnly? EndTime { get; init; }

    public CalendarCategory Category { get; init; } = CalendarCategory.Meeting;

    public CalendarAudience Audience { get; init; } = CalendarAudience.People;

    // Ignored for a company event, which is for everybody by definition — and
    // refused rather than ignored if one is sent, because quietly dropping it
    // would leave somebody sure they had invited five people to something that
    // went to the whole company.
    public IReadOnlyList<Guid> EmployeeIds { get; init; } = [];
}

public enum CalendarEventOutcome
{
    Succeeded,
    NotFound,

    // The draft broke one of the domain's rules; Refusal says which.
    Refused,

    // An attendee who does not work here, or has left.
    EmployeeNotFound,

    // Putting something on somebody else's calendar when they are neither you
    // nor one of your reports.
    NotYoursToAddTo,

    // Editing or deleting an event somebody else created.
    NotYoursToChange,
}

public sealed record CalendarEventResult
{
    public required CalendarEventOutcome Outcome { get; init; }

    public CalendarEventDto? Event { get; init; }

    // Set only when the outcome is Refused.
    public CalendarRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == CalendarEventOutcome.Succeeded;

    public static CalendarEventResult Failed(CalendarEventOutcome outcome) =>
        new() { Outcome = outcome };

    public static CalendarEventResult Refused(CalendarRefusal refusal) =>
        new() { Outcome = CalendarEventOutcome.Refused, Refusal = refusal };

    public static CalendarEventResult Ok(CalendarEventDto calendarEvent) =>
        new() { Outcome = CalendarEventOutcome.Succeeded, Event = calendarEvent };
}
