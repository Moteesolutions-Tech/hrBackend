using Motee.Domain.Employees;

namespace Motee.Tests.Employees;

// HR's private notes about one person.
//
// Two things the screen gets wrong. Visibility is rendered as a pill and
// filtered on nothing, so a note marked HR-only is shown to whoever opened the
// tab — and it is the field that decides whether writing something frank is
// safe. And a reminder carries a date that reminds nobody.
//
// Written before the rules.
public class EmployeeNoteTests
{
    private static readonly DateOnly Today = new(2026, 9, 22);

    private static EmployeeNote Note(
        string body = "Third late start this month — worth asking if something is going on.",
        NoteKind kind = NoteKind.Note,
        NoteAudience audience = NoteAudience.HrOnly,
        DateOnly? remindAt = null,
        DateTimeOffset? remindedAt = null,
        bool pinned = false,
        DateTimeOffset? createdAt = null) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            EmployeeId = Guid.NewGuid(),
            Body = body,
            Kind = kind,
            Audience = audience,
            RemindAt = remindAt,
            RemindedAt = remindedAt,
            Pinned = pinned,
            CreatedAt = createdAt ?? new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero),
        };

    // ── who may read one ───────────────────────────────────────────────────

    // The pill the screen renders and nothing filters on.
    [Fact]
    public void AnHrOnlyNoteIsNotReadableByAManager()
    {
        Assert.False(EmployeeNotes.IsReadableByManager(Note()));
    }

    [Fact]
    public void ANoteMarkedForManagersIs()
    {
        Assert.True(EmployeeNotes.IsReadableByManager(
            Note(audience: NoteAudience.ManagerAndHr)));
    }

    // Private by default. A note written without thinking about the setting is
    // the one most likely to need protecting.
    [Fact]
    public void ANoteIsPrivateUnlessSomebodySaysOtherwise()
    {
        Assert.Equal(NoteAudience.HrOnly, new EmployeeNote { Body = "x" }.Audience);
    }

    // ── what is refused ────────────────────────────────────────────────────

    [Fact]
    public void ANoteNeedsSomethingInIt()
    {
        Assert.Equal(NoteRefusal.BodyRequired, EmployeeNotes.Check(Note(body: "   "), Today));
    }

    [Fact]
    public void ANoteHasACeiling()
    {
        Assert.Equal(
            NoteRefusal.BodyTooLong,
            EmployeeNotes.Check(
                Note(body: new string('x', EmployeeNotes.MaximumBodyLength + 1)), Today));
    }

    // A reminder with no date reminds nobody, which is the whole of what
    // separates it from a note.
    [Fact]
    public void AReminderNeedsADate()
    {
        Assert.Equal(
            NoteRefusal.ReminderNeedsADate,
            EmployeeNotes.Check(Note(kind: NoteKind.Reminder), Today));
    }

    // It would fire on the next sweep and read as though the system had been
    // late.
    [Fact]
    public void AReminderCannotBeSetForYesterday()
    {
        Assert.Equal(
            NoteRefusal.ReminderInThePast,
            EmployeeNotes.Check(
                Note(kind: NoteKind.Reminder, remindAt: Today.AddDays(-1)), Today));
    }

    // "Remind me this morning" is a real thing to ask for.
    [Fact]
    public void AReminderForTodayIsFine()
    {
        Assert.Equal(
            NoteRefusal.None,
            EmployeeNotes.Check(Note(kind: NoteKind.Reminder, remindAt: Today), Today));
    }

    [Fact]
    public void APlainNoteNeedsNoDate()
    {
        Assert.Equal(NoteRefusal.None, EmployeeNotes.Check(Note(), Today));
    }

    // ── when a reminder fires ──────────────────────────────────────────────

    [Fact]
    public void AReminderReachedIsDue()
    {
        Assert.True(EmployeeNotes.ReminderIsDue(
            Note(kind: NoteKind.Reminder, remindAt: Today), Today));
    }

    [Fact]
    public void AReminderStillAheadIsNot()
    {
        Assert.False(EmployeeNotes.ReminderIsDue(
            Note(kind: NoteKind.Reminder, remindAt: Today.AddDays(7)), Today));
    }

    // Without this the sweep tells somebody every night until they dismiss it.
    [Fact]
    public void AReminderAlreadyRaisedDoesNotFireAgain()
    {
        Assert.False(EmployeeNotes.ReminderIsDue(
            Note(
                kind: NoteKind.Reminder,
                remindAt: Today.AddDays(-3),
                remindedAt: DateTimeOffset.UtcNow),
            Today));
    }

    [Fact]
    public void APlainNoteNeverFires()
    {
        Assert.False(EmployeeNotes.ReminderIsDue(Note(remindAt: Today), Today));
    }

    // ── order ──────────────────────────────────────────────────────────────

    // The screen renders the pin icon without sorting by it, so a pinned note
    // sinks down the list as others are added — which is the one thing pinning
    // is for.
    [Fact]
    public void APinnedNoteComesFirstHoweverOldItIs()
    {
        EmployeeNote pinned = Note(
            pinned: true, createdAt: new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));

        EmployeeNote recent = Note(
            createdAt: new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero));

        IReadOnlyList<EmployeeNote> ordered = EmployeeNotes.InReadingOrder([recent, pinned]);

        Assert.Equal(pinned.Id, ordered[0].Id);
    }

    [Fact]
    public void OtherwiseTheNewestComesFirst()
    {
        EmployeeNote older = Note(
            createdAt: new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero));

        EmployeeNote newer = Note(
            createdAt: new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));

        IReadOnlyList<EmployeeNote> ordered = EmployeeNotes.InReadingOrder([older, newer]);

        Assert.Equal(newer.Id, ordered[0].Id);
    }

    [Fact]
    public void AnEmptyListOrdersToNothing()
    {
        Assert.Empty(EmployeeNotes.InReadingOrder([]));
    }
}
