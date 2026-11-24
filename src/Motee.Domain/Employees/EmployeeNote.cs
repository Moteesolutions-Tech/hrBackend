using Motee.Domain.Common;

namespace Motee.Domain.Employees;

public enum NoteKind
{
    Note,

    // Carries a date to be reminded on. A reminder with nothing that reminds
    // anybody is a note with a date on it, which is why RemindAt is required
    // and why the nightly sweep reads these.
    Reminder,
}

// Who may read a note.
//
// The screen renders this as a pill beside the note and filters on nothing, so
// a note marked HR-only is shown to whoever opened the tab. It is the field
// that decides whether writing something frank about somebody is safe, so it
// has to be the field that actually withholds it.
//
// Named for the audience rather than "visibility", because Relations already
// has a NoteVisibility for case notes and two enums of that name in one
// assembly is a using-directive away from somebody applying the wrong rule.
public enum NoteAudience
{
    HrOnly,

    ManagerAndHr,
}

// An HR note or reminder about one person.
//
// Private by default and private in fact. These are the notes somebody writes
// before a difficult conversation — "third late start this month, worth asking
// if something is going on" — and the whole value of the feature is that it can
// be written honestly.
public class EmployeeNote : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    // Who wrote it. Recorded from the request rather than taken from the
    // payload: the author of a private note is not something a client gets to
    // claim.
    public Guid? AuthorUserId { get; set; }

    // Copied, not joined — who wrote this two years ago has to keep reading
    // correctly after that person leaves.
    public string? AuthorName { get; set; }

    public required string Body { get; set; }

    public NoteKind Kind { get; set; } = NoteKind.Note;

    public NoteAudience Audience { get; set; } = NoteAudience.HrOnly;

    public bool Pinned { get; set; }

    public DateOnly? RemindAt { get; set; }

    // When the reminder was raised, so the nightly sweep does not raise it
    // again every night for ever.
    public DateTimeOffset? RemindedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum NoteRefusal
{
    None,
    BodyRequired,
    BodyTooLong,
    ReminderNeedsADate,
    ReminderInThePast,
    EmployeeNotFound,
    NotYours,
}

public static class EmployeeNotes
{
    public const int MaximumBodyLength = 4000;

    // Whether somebody whose reach is their own reports may read this note.
    //
    // HR reads everything; a line manager reads only what was marked for them.
    // The visibility field exists for exactly this question and nothing asked
    // it before.
    public static bool IsReadableByManager(EmployeeNote note) =>
        note.Audience == NoteAudience.ManagerAndHr;

    public static NoteRefusal Check(EmployeeNote note, DateOnly today)
    {
        string body = (note.Body ?? string.Empty).Trim();

        if (body.Length == 0)
        {
            return NoteRefusal.BodyRequired;
        }

        if (body.Length > MaximumBodyLength)
        {
            return NoteRefusal.BodyTooLong;
        }

        if (note.Kind != NoteKind.Reminder)
        {
            return NoteRefusal.None;
        }

        // A reminder with no date reminds nobody, which is the whole of what
        // separates it from a note.
        if (note.RemindAt is not DateOnly remind)
        {
            return NoteRefusal.ReminderNeedsADate;
        }

        // Today is fine — "remind me this morning" is a real thing to ask for.
        // Yesterday is not: it would fire on the next sweep and read as though
        // the system had been late.
        return remind < today ? NoteRefusal.ReminderInThePast : NoteRefusal.None;
    }

    // Due, and not already raised. Both halves matter: without the first the
    // sweep tells people about reminders they set for next month, and without
    // the second it tells them every night until they dismiss it.
    public static bool ReminderIsDue(EmployeeNote note, DateOnly today) =>
        note.Kind == NoteKind.Reminder
            && note.RemindedAt is null
            && note.RemindAt is DateOnly remind
            && remind <= today;

    // Pinned first, then newest. The pin is the only ordering control the
    // screen has, and it renders the icon without sorting by it — so a pinned
    // note sinks down the list as others are added, which is the one thing
    // pinning is for.
    public static IReadOnlyList<EmployeeNote> InReadingOrder(
        IEnumerable<EmployeeNote> notes) =>
        [
            .. notes
                .OrderByDescending(note => note.Pinned)
                .ThenByDescending(note => note.CreatedAt),
        ];
}
