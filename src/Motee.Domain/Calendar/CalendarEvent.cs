using Motee.Domain.Common;

namespace Motee.Domain.Calendar;

// Which pane of the calendar an item belongs in. The sidebar turns these on and
// off, so an item's layer decides whether hiding "my schedule" takes it away.
public enum CalendarLayer
{
    Schedule,
    Company,
    Holidays,
}

// Who an event is for.
//
// The distinction the frontend's dialog offers — "People's calendars" against
// "Company calendar: everyone sees it" — held as a property of the event rather
// than inferred from whether a guest list happens to be empty.
public enum CalendarAudience
{
    Company,
    People,
}

// The whole vocabulary the calendar reads in, derived items included.
//
// One enum rather than two because the colour key, the legend and the filter
// chips are one list to the person using them; which members may be *written*
// is a rule, below, not a second type.
public enum CalendarCategory
{
    Meeting,
    OneToOne,
    Company,
    Deadline,
    Reminder,
    Training,
    Performance,
    Personal,

    // ── derived from other records ──────────────────────────────────────────
    Holiday,
    Birthday,
    Anniversary,
    Leave,
    LeaveRequest,
    Cover,
    Interview,
    Review,
}

public static class CalendarCategories
{
    // A derived category describes a record that lives somewhere else: a public
    // holiday, a date of birth, an approved leave request. Letting somebody type
    // one by hand would put a leave day in the calendar with no leave request
    // behind it — visible to their manager, counted by nobody, and deductible
    // from no balance.
    public static bool Choosable(CalendarCategory category) => category
        is CalendarCategory.Meeting
        or CalendarCategory.OneToOne
        or CalendarCategory.Company
        or CalendarCategory.Deadline
        or CalendarCategory.Reminder
        or CalendarCategory.Training
        or CalendarCategory.Performance
        or CalendarCategory.Personal;
}

public enum CalendarRefusal
{
    None,
    TitleRequired,
    EndsBeforeItStarts,
    StartTimeRequired,
    EndTimeNotAfterStart,

    // An event on nobody's calendar is on no calendar.
    NobodyToAddItTo,

    // Company means everyone, so a guest list alongside it is two different
    // intentions in one request.
    CompanyEventTakesNoAttendees,

    CategoryNotChoosable,

    // Almost always a mistyped year. An event that long paints every day of
    // every view it touches.
    LongerThanAYear,
}

// Something somebody put in the calendar.
//
// Only hand-made events are rows. Leave, cover, holidays, birthdays, work
// anniversaries, interviews and review due dates are read from the records that
// already hold them — copying those into here would create a second answer to
// "is Bola off on Thursday", and the two would drift the first time a request
// was cancelled.
public class CalendarEvent : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public DateOnly Date { get; set; }

    // Inclusive, and null for the ordinary single-day case rather than a copy of
    // Date: "ends when it starts" and "has no end" are the same thing here, and
    // one of them would otherwise have to be kept in step on every edit.
    public DateOnly? EndDate { get; set; }

    public bool AllDay { get; set; }

    public TimeOnly? StartTime { get; set; }

    public TimeOnly? EndTime { get; set; }

    public CalendarCategory Category { get; set; } = CalendarCategory.Meeting;

    public CalendarAudience Audience { get; set; } = CalendarAudience.People;

    // Who added it. Kept so they can still see and remove an event they put on
    // other people's calendars, which they are not otherwise an attendee of.
    public Guid? CreatedByEmployeeId { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// One person an event was put on. Company events have none — they are for
// everybody, and listing the whole company would need rewriting on every joiner.
public class CalendarEventAttendee : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CalendarEventId { get; set; }

    public Guid EmployeeId { get; set; }
}

public static class CalendarEvents
{
    private const int LongestRunDays = 366;

    public static CalendarRefusal Refusal(
        CalendarEvent draft,
        IReadOnlyCollection<Guid> attendees)
    {
        if (string.IsNullOrWhiteSpace(draft.Title))
        {
            return CalendarRefusal.TitleRequired;
        }

        if (!CalendarCategories.Choosable(draft.Category))
        {
            return CalendarRefusal.CategoryNotChoosable;
        }

        DateOnly last = draft.EndDate ?? draft.Date;

        if (last < draft.Date)
        {
            return CalendarRefusal.EndsBeforeItStarts;
        }

        if (last.DayNumber - draft.Date.DayNumber > LongestRunDays)
        {
            return CalendarRefusal.LongerThanAYear;
        }

        CalendarRefusal clock = Clock(draft, last);

        if (clock != CalendarRefusal.None)
        {
            return clock;
        }

        return draft.Audience switch
        {
            CalendarAudience.People when attendees.Count == 0 => CalendarRefusal.NobodyToAddItTo,
            CalendarAudience.Company when attendees.Count > 0 =>
                CalendarRefusal.CompanyEventTakesNoAttendees,
            _ => CalendarRefusal.None,
        };
    }

    // Times only mean anything on a timed, single-day event. An all-day event
    // ignores whatever came with it, and an overnight one ending at 09:00 the
    // next morning is perfectly ordinary.
    private static CalendarRefusal Clock(CalendarEvent draft, DateOnly last)
    {
        if (draft.AllDay)
        {
            return CalendarRefusal.None;
        }

        if (draft.StartTime is not TimeOnly start)
        {
            return CalendarRefusal.StartTimeRequired;
        }

        return last == draft.Date && draft.EndTime is TimeOnly end && end <= start
            ? CalendarRefusal.EndTimeNotAfterStart
            : CalendarRefusal.None;
    }

    public static bool Spans(DateOnly date, DateOnly? endDate, DateOnly day) =>
        day >= date && day <= (endDate ?? date);

    public static bool Overlaps(DateOnly date, DateOnly? endDate, DateOnly from, DateOnly to) =>
        date <= to && (endDate ?? date) >= from;

    // Birthdays and anniversaries are about a person but belong to the whole
    // office, so they ride with the company layer. Filing them under "my
    // schedule" would mean turning your own calendar off took the office
    // birthdays with it.
    public static CalendarLayer LayerOf(CalendarCategory category, CalendarAudience audience) =>
        category switch
        {
            CalendarCategory.Holiday => CalendarLayer.Holidays,
            CalendarCategory.Birthday or CalendarCategory.Anniversary => CalendarLayer.Company,
            _ => audience == CalendarAudience.Company
                ? CalendarLayer.Company
                : CalendarLayer.Schedule,
        };
}
