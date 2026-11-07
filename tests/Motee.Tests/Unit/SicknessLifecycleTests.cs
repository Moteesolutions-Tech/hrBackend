using Motee.Domain.Sickness;

namespace Motee.Tests.Unit;

// An absence from the day it is reported to the day the return-to-work is
// signed off, and the fit-note status derived along the way.
//
// The fit-note rule is the one that matters most here. The frontend works it
// out once, when the absence is reported, and then lets HR hand-edit it — so
// somebody who goes off sick for three days and is still off three weeks later
// keeps reading "self-certified", and never appears on the fit-notes-overdue
// list that exists to catch exactly that. Derived on read, it changes by
// itself on day eight.
public class SicknessLifecycleTests
{
    private static readonly DateOnly Today = new(2026, 6, 30);

    private static SicknessRecord Off(
        string start = "2026-06-29",
        string? end = null,
        SicknessKind kind = SicknessKind.Sickness,
        SicknessApprovalStatus status = SicknessApprovalStatus.AwaitingApproval,
        RtwStatus rtw = RtwStatus.NotDue,
        bool interviewRequired = false,
        IReadOnlyList<SicknessDocument>? documents = null) => new()
        {
            Id = Guid.NewGuid(),
            EmployeeId = Guid.NewGuid(),
            Kind = kind,
            StartDate = DateOnly.Parse(start),
            EndDate = end is null ? null : DateOnly.Parse(end),
            ApprovalStatus = status,
            AbsenceState = end is null ? AbsenceState.Ongoing : AbsenceState.Returned,
            Reason = SicknessReason.ColdOrFlu,
            NotifiedAt = DateTimeOffset.UtcNow,
            Source = SicknessSource.Self,
            ReportedAt = DateTimeOffset.UtcNow,
            RtwStatus = rtw,
            RtwInterviewRequired = interviewRequired,
            Documents = [.. documents ?? []],
        };

    private static SicknessDocument FitNote(string? validTo) => new()
    {
        Id = Guid.NewGuid(),
        Kind = SicknessDocumentKind.FitNote,
        Name = "fit-note.pdf",
        ValidFrom = new DateOnly(2026, 6, 1),
        ValidTo = validTo is null ? null : DateOnly.Parse(validTo),
        UploadedAt = DateTimeOffset.UtcNow,
    };

    // ── approving ───────────────────────────────────────────────────────────

    [Fact]
    public void AnAbsenceWaitingForApprovalCanBeApproved() =>
        Assert.Equal(SicknessRefusal.None, SicknessLifecycle.CanApprove(Off()));

    [Theory]
    [InlineData(SicknessApprovalStatus.Approved)]
    [InlineData(SicknessApprovalStatus.Rejected)]
    [InlineData(SicknessApprovalStatus.Cancelled)]
    public void AnAbsenceAlreadyDecidedCannotBeApprovedAgain(SicknessApprovalStatus status) =>
        Assert.Equal(
            SicknessRefusal.NotAwaitingApproval,
            SicknessLifecycle.CanApprove(Off(status: status)));

    [Fact]
    public void AnAbsenceWaitingForApprovalCanBeRejected() =>
        Assert.Equal(SicknessRefusal.None, SicknessLifecycle.CanReject(Off()));

    [Fact]
    public void AnApprovedAbsenceCannotBeRejectedAfterTheFact() =>
        Assert.Equal(
            SicknessRefusal.NotAwaitingApproval,
            SicknessLifecycle.CanReject(Off(status: SicknessApprovalStatus.Approved)));

    // ── cancelling ──────────────────────────────────────────────────────────

    // Approved absence can still be cancelled: it was logged in error, or the
    // person turned out not to be off at all. Refusing that would leave a day
    // of sickness on somebody's Bradford score with no way to remove it.
    [Theory]
    [InlineData(SicknessApprovalStatus.AwaitingApproval)]
    [InlineData(SicknessApprovalStatus.Approved)]
    public void AnAbsenceThatIsStillLiveCanBeCancelled(SicknessApprovalStatus status) =>
        Assert.Equal(
            SicknessRefusal.None, SicknessLifecycle.CanCancel(Off(status: status)));

    [Theory]
    [InlineData(SicknessApprovalStatus.Rejected)]
    [InlineData(SicknessApprovalStatus.Cancelled)]
    public void AnAbsenceAlreadyOffTheBooksCannotBeCancelled(SicknessApprovalStatus status) =>
        Assert.Equal(
            SicknessRefusal.AlreadyClosed, SicknessLifecycle.CanCancel(Off(status: status)));

    // ── coming back ─────────────────────────────────────────────────────────

    [Fact]
    public void AnOngoingAbsenceCanBeClosedWithALastSickDay() =>
        Assert.Equal(
            SicknessRefusal.None,
            SicknessLifecycle.CanReturn(Off("2026-06-22"), new DateOnly(2026, 6, 26), null));

    [Fact]
    public void ALastSickDayBeforeTheAbsenceStartedIsRefused() =>
        Assert.Equal(
            SicknessRefusal.LastDayBeforeStart,
            SicknessLifecycle.CanReturn(Off("2026-06-22"), new DateOnly(2026, 6, 21), null));

    [Fact]
    public void AReturnDateOnOrBeforeTheLastSickDayIsRefused() =>
        Assert.Equal(
            SicknessRefusal.ReturnBeforeLastDay,
            SicknessLifecycle.CanReturn(
                Off("2026-06-22"), new DateOnly(2026, 6, 26), new DateOnly(2026, 6, 26)));

    [Fact]
    public void AnAbsenceAlreadyClosedCannotBeClosedAgain() =>
        Assert.Equal(
            SicknessRefusal.AlreadyReturned,
            SicknessLifecycle.CanReturn(
                Off("2026-06-22", "2026-06-26"), new DateOnly(2026, 6, 26), null));

    [Fact]
    public void ARefusedAbsenceHasNoReturnToRecord() =>
        Assert.Equal(
            SicknessRefusal.AlreadyClosed,
            SicknessLifecycle.CanReturn(
                Off("2026-06-22", status: SicknessApprovalStatus.Rejected),
                new DateOnly(2026, 6, 26),
                null));

    // ── the return-to-work interview ────────────────────────────────────────

    [Fact]
    public void AReturnToWorkIsCompletedOnceTheInterviewIsHeldAndItIsSafe() =>
        Assert.Equal(
            SicknessRefusal.None,
            SicknessLifecycle.CanCompleteReturnToWork(
                Off("2026-06-22", "2026-06-26", rtw: RtwStatus.Pending, interviewRequired: true),
                interviewHeld: true,
                safeToReturn: true));

    [Fact]
    public void AReturnToWorkCannotBeSignedOffWhileThePersonIsStillOff() =>
        Assert.Equal(
            SicknessRefusal.StillOngoing,
            SicknessLifecycle.CanCompleteReturnToWork(
                Off("2026-06-22", rtw: RtwStatus.NotDue), true, true));

    [Fact]
    public void AReturnToWorkNeedingAnInterviewIsNotDoneWithoutOne() =>
        Assert.Equal(
            SicknessRefusal.InterviewNotHeld,
            SicknessLifecycle.CanCompleteReturnToWork(
                Off("2026-06-22", "2026-06-26", rtw: RtwStatus.Pending, interviewRequired: true),
                interviewHeld: false,
                safeToReturn: true));

    // Somebody has to say so, on the record. It is the one question a
    // return-to-work exists to answer.
    [Fact]
    public void AReturnToWorkIsNotDoneUntilSomebodyConfirmsItIsSafe() =>
        Assert.Equal(
            SicknessRefusal.SafetyNotConfirmed,
            SicknessLifecycle.CanCompleteReturnToWork(
                Off("2026-06-22", "2026-06-26", rtw: RtwStatus.Pending),
                interviewHeld: false,
                safeToReturn: false));

    [Fact]
    public void AReturnToWorkWithNoInterviewRequiredNeedsOnlyTheSafetyCheck() =>
        Assert.Equal(
            SicknessRefusal.None,
            SicknessLifecycle.CanCompleteReturnToWork(
                Off("2026-06-22", "2026-06-26", rtw: RtwStatus.Pending),
                interviewHeld: false,
                safeToReturn: true));

    [Fact]
    public void AReturnToWorkAlreadySignedOffIsNotSignedOffTwice() =>
        Assert.Equal(
            SicknessRefusal.AlreadyCompleted,
            SicknessLifecycle.CanCompleteReturnToWork(
                Off("2026-06-22", "2026-06-26", rtw: RtwStatus.Completed), true, true));

    // ── whether an interview is due at all ──────────────────────────────────

    [Theory]
    [InlineData(RtwInterviewRule.Always, 1, true)]
    [InlineData(RtwInterviewRule.Never, 40, false)]
    [InlineData(RtwInterviewRule.AfterDays, 2, false)]
    [InlineData(RtwInterviewRule.AfterDays, 3, true)]
    [InlineData(RtwInterviewRule.AfterDays, 10, true)]
    public void WhetherAnInterviewIsNeededFollowsThePolicyAndTheLength(
        RtwInterviewRule rule, int days, bool expected) =>
        Assert.Equal(expected, SicknessLifecycle.InterviewRequired(rule, days, afterDays: 3));

    // ── fit notes, derived ──────────────────────────────────────────────────

    [Fact]
    public void AShortAbsenceIsSelfCertified() =>
        Assert.Equal(
            FitNoteStatus.SelfCertified,
            FitNotes.StatusOf(Off("2026-06-28", "2026-06-30"), Today, selfCertDays: 7));

    // Seven calendar days is the UK limit, and the seventh day is still inside
    // it. An off-by-one here asks a quarter of absences for a note they do not
    // need.
    [Fact]
    public void TheSeventhDayIsStillSelfCertified() =>
        Assert.Equal(
            FitNoteStatus.SelfCertified,
            FitNotes.StatusOf(Off("2026-06-24", "2026-06-30"), Today, selfCertDays: 7));

    [Fact]
    public void TheEighthDayNeedsAFitNote() =>
        Assert.Equal(
            FitNoteStatus.Awaiting,
            FitNotes.StatusOf(Off("2026-06-23", "2026-06-30"), Today, selfCertDays: 7));

    // The bug this derivation exists to fix: nobody edits the record, the
    // absence simply goes on, and on day eight it starts asking for a note.
    [Fact]
    public void AnOngoingAbsenceStartsAskingForANoteWhenItPassesTheLimit()
    {
        SicknessRecord running = Off("2026-06-28");

        Assert.Equal(
            FitNoteStatus.SelfCertified,
            FitNotes.StatusOf(running, new DateOnly(2026, 6, 30), selfCertDays: 7));

        Assert.Equal(
            FitNoteStatus.Awaiting,
            FitNotes.StatusOf(running, new DateOnly(2026, 7, 6), selfCertDays: 7));
    }

    [Fact]
    public void AFitNoteCoveringTheWholeAbsenceIsReceived() =>
        Assert.Equal(
            FitNoteStatus.Received,
            FitNotes.StatusOf(
                Off("2026-06-01", "2026-06-20", documents: [FitNote("2026-06-20")]),
                Today,
                selfCertDays: 7));

    [Fact]
    public void AFitNoteThatRanOutBeforeTheAbsenceDidHasExpired() =>
        Assert.Equal(
            FitNoteStatus.Expired,
            FitNotes.StatusOf(
                Off("2026-06-01", "2026-06-20", documents: [FitNote("2026-06-14")]),
                Today,
                selfCertDays: 7));

    [Fact]
    public void AnOngoingAbsenceOutlivingItsFitNoteHasExpired() =>
        Assert.Equal(
            FitNoteStatus.Expired,
            FitNotes.StatusOf(
                Off("2026-06-01", documents: [FitNote("2026-06-14")]),
                Today,
                selfCertDays: 7));

    // Two notes, one after the other, is the normal way a long absence is
    // covered. The latest one is the one that decides.
    [Fact]
    public void TheLatestFitNoteIsTheOneThatCounts() =>
        Assert.Equal(
            FitNoteStatus.Received,
            FitNotes.StatusOf(
                Off("2026-06-01", documents: [FitNote("2026-06-14"), FitNote("2026-07-14")]),
                Today,
                selfCertDays: 7));

    // A note with no end date cannot be judged expired, and guessing it has
    // would chase somebody for a document they already sent.
    [Fact]
    public void AFitNoteWithNoEndDateIsTakenAsCurrent() =>
        Assert.Equal(
            FitNoteStatus.Received,
            FitNotes.StatusOf(
                Off("2026-06-01", documents: [FitNote(null)]), Today, selfCertDays: 7));

    [Fact]
    public void APlannedProcedureNeedsNoFitNote() =>
        Assert.Equal(
            FitNoteStatus.NotRequired,
            FitNotes.StatusOf(
                Off("2026-06-01", "2026-06-20", kind: SicknessKind.MedicalProcedure),
                Today,
                selfCertDays: 7));

    [Theory]
    [InlineData(SicknessApprovalStatus.Rejected)]
    [InlineData(SicknessApprovalStatus.Cancelled)]
    public void AnAbsenceOffTheBooksNeedsNoFitNote(SicknessApprovalStatus status) =>
        Assert.Equal(
            FitNoteStatus.NotRequired,
            FitNotes.StatusOf(
                Off("2026-06-01", "2026-06-20", status: status), Today, selfCertDays: 7));

    [Fact]
    public void AnOutstandingFitNoteIsTheOneWorthChasing()
    {
        Assert.True(FitNotes.IsOutstanding(FitNoteStatus.Awaiting));
        Assert.True(FitNotes.IsOutstanding(FitNoteStatus.Expired));
        Assert.False(FitNotes.IsOutstanding(FitNoteStatus.Received));
        Assert.False(FitNotes.IsOutstanding(FitNoteStatus.SelfCertified));
        Assert.False(FitNotes.IsOutstanding(FitNoteStatus.NotRequired));
    }
}
