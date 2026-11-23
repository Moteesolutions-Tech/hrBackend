using Motee.Domain.Relations;

namespace Motee.Tests.Relations;

// What a case will accept, who can read it, where it stands against its target,
// and how its number is minted.
//
// Confidentiality is the part that has to be enforced rather than displayed. A
// restricted case is named individuals only, and the client shows the level as
// a badge and then lists every case to anybody with the module.
//
// Written before the rules.
public class CaseRuleTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    private static readonly Guid Hr = Guid.NewGuid();
    private static readonly Guid Investigator = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Subject = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();

    private static ErCase Of(
        CaseStage stage = CaseStage.Raised,
        string description = "Repeated lateness over six weeks.",
        ConfidentialityLevel confidentiality = ConfidentialityLevel.Standard,
        DateOnly? raisedOn = null,
        DateOnly? targetResolutionOn = null,
        DateOnly? closedOn = null,
        Guid? investigatorId = null,
        Guid? ownerId = null,
        Guid? subjectId = null) => new()
        {
            CaseNumber = "ERC-001",
            ComplaintType = CaseComplaintType.Disciplinary,
            Stage = stage,
            Description = description,
            ConfidentialityLevel = confidentiality,
            RaisedOn = raisedOn ?? Today.AddDays(-10),
            TargetResolutionOn = targetResolutionOn,
            ClosedOn = closedOn,
            InvestigatorEmployeeId = investigatorId,
            CaseOwnerEmployeeId = ownerId,
            SubjectEmployeeId = subjectId ?? Subject,
        };

    // ── raising one ─────────────────────────────────────────────────────────

    [Fact]
    public void AcceptsACase()
    {
        Assert.Equal(CaseRefusal.None, CaseRules.Refusal(Of()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Late")]
    public void RefusesACaseWithNothingToGoOn(string description)
    {
        // A case is a record somebody will be asked to act on. Five words is
        // not a record.
        Assert.Equal(
            CaseRefusal.DescriptionTooShort,
            CaseRules.Refusal(Of(description: description)));
    }

    [Fact]
    public void RefusesACaseAboutNobody()
    {
        ErCase anonymous = Of();
        anonymous.SubjectEmployeeId = null;

        Assert.Equal(CaseRefusal.SubjectRequired, CaseRules.Refusal(anonymous));
    }

    [Fact]
    public void RefusesAnIncidentDateInTheFuture()
    {
        ErCase impossible = Of();
        impossible.IncidentOn = Today.AddDays(1);

        Assert.Equal(
            CaseRefusal.IncidentInTheFuture, CaseRules.Refusal(impossible, Today));
    }

    [Fact]
    public void AcceptsAnIncidentToday()
    {
        ErCase today = Of();
        today.IncidentOn = Today;

        Assert.Equal(CaseRefusal.None, CaseRules.Refusal(today, Today));
    }

    [Fact]
    public void RefusesAnInvestigatorWhoIsTheSubject()
    {
        // Investigating yourself is not an investigation.
        Assert.Equal(
            CaseRefusal.InvestigatorIsSubject,
            CaseRules.Refusal(Of(investigatorId: Subject)));
    }

    [Fact]
    public void RefusesACaseOwnerWhoIsTheSubject()
    {
        Assert.Equal(
            CaseRefusal.OwnerIsSubject, CaseRules.Refusal(Of(ownerId: Subject)));
    }

    // ── the case number ─────────────────────────────────────────────────────

    // Minted here rather than from the length of a list, which hands two people
    // raising a case at the same moment the same number.
    [Fact]
    public void NumbersTheFirstCase()
    {
        Assert.Equal("ERC-001", CaseNumbers.Next(null));
    }

    [Fact]
    public void NumbersTheNextCaseAfterTheHighestSoFar()
    {
        Assert.Equal("ERC-002", CaseNumbers.Next("ERC-001"));
        Assert.Equal("ERC-043", CaseNumbers.Next("ERC-042"));
    }

    [Fact]
    public void KeepsCountingPastAThousand()
    {
        Assert.Equal("ERC-1000", CaseNumbers.Next("ERC-999"));
    }

    [Fact]
    public void IgnoresANumberItCannotRead()
    {
        // A hand-edited or imported number should not stop the next case being
        // raised.
        Assert.Equal("ERC-001", CaseNumbers.Next("legacy/7"));
    }

    // ── which tab a case shows on ───────────────────────────────────────────

    [Fact]
    public void AGrievanceShowsOnTheGrievanceSide()
    {
        IReadOnlyList<CaseComplaintType> raised = CaseGroups.Of(CaseGroup.Grievance);

        Assert.Contains(CaseComplaintType.PayDispute, raised);
        Assert.Contains(CaseComplaintType.Whistleblowing, raised);
        Assert.DoesNotContain(CaseComplaintType.Misconduct, raised);
    }

    [Fact]
    public void ADisciplinaryShowsOnTheDisciplinarySide()
    {
        IReadOnlyList<CaseComplaintType> put = CaseGroups.Of(CaseGroup.Disciplinary);

        Assert.Contains(CaseComplaintType.Capability, put);
        Assert.Contains(CaseComplaintType.Attendance, put);
        Assert.DoesNotContain(CaseComplaintType.PayDispute, put);
    }

    // Either side can end in one, so hiding it from a tab would hide a case
    // that plainly concerns the person.
    [Theory]
    [InlineData(CaseComplaintType.Appeal)]
    [InlineData(CaseComplaintType.Investigation)]
    public void AnAppealOrAnInvestigationShowsOnBoth(CaseComplaintType type)
    {
        Assert.Contains(type, CaseGroups.Of(CaseGroup.Grievance));
        Assert.Contains(type, CaseGroups.Of(CaseGroup.Disciplinary));
    }

    [Fact]
    public void EveryKindOfCaseShowsSomewhere()
    {
        IReadOnlyList<CaseComplaintType> both =
        [
            .. CaseGroups.Of(CaseGroup.Grievance),
            .. CaseGroups.Of(CaseGroup.Disciplinary),
        ];

        Assert.All(
            Enum.GetValues<CaseComplaintType>(),
            type => Assert.Contains(type, both));
    }

    // ── where it stands ─────────────────────────────────────────────────────

    [Fact]
    public void ACaseWithNoTargetHasNothingToBeMeasuredAgainst()
    {
        Assert.Equal(SlaState.None, CaseSla.StateOf(Of(), Today));
    }

    [Fact]
    public void ACaseWithRoomLeftIsOnTrack()
    {
        Assert.Equal(
            SlaState.OnTrack,
            CaseSla.StateOf(Of(targetResolutionOn: Today.AddDays(10)), Today));
    }

    [Fact]
    public void ACaseInsideThreeDaysIsDueSoon()
    {
        Assert.Equal(
            SlaState.DueSoon,
            CaseSla.StateOf(Of(targetResolutionOn: Today.AddDays(3)), Today));
    }

    [Fact]
    public void ACasePastItsTargetIsOverdue()
    {
        Assert.Equal(
            SlaState.Overdue,
            CaseSla.StateOf(Of(targetResolutionOn: Today.AddDays(-1)), Today));
    }

    // The client reports every closed case as on track. A case that closed
    // forty days late was not on track, and HR needs to know which ones ran
    // over.
    [Fact]
    public void AClosedCaseIsJudgedOnWhetherItActuallyMetItsTarget()
    {
        Assert.Equal(
            SlaState.OnTrack,
            CaseSla.StateOf(
                Of(
                    stage: CaseStage.Closed,
                    targetResolutionOn: Today.AddDays(-5),
                    closedOn: Today.AddDays(-7)),
                Today));

        Assert.Equal(
            SlaState.Overdue,
            CaseSla.StateOf(
                Of(
                    stage: CaseStage.Closed,
                    targetResolutionOn: Today.AddDays(-20),
                    closedOn: Today.AddDays(-2)),
                Today));
    }

    [Fact]
    public void AClosedCaseWithNoTargetIsStillUnmeasured()
    {
        Assert.Equal(
            SlaState.None,
            CaseSla.StateOf(
                Of(stage: CaseStage.Closed, closedOn: Today.AddDays(-2)), Today));
    }

    [Fact]
    public void CountsTheDaysACaseHasBeenOpen()
    {
        Assert.Equal(10, CaseSla.DaysOpen(Of(raisedOn: Today.AddDays(-10)), Today));
    }

    // The clock stops when it closes, so the figure does not keep climbing on a
    // case nobody is working on.
    [Fact]
    public void TheClockStopsWhenACaseCloses()
    {
        ErCase closed = Of(
            stage: CaseStage.Closed,
            raisedOn: Today.AddDays(-30),
            closedOn: Today.AddDays(-10));

        Assert.Equal(20, CaseSla.DaysOpen(closed, Today));
    }

    // ── who can read it ─────────────────────────────────────────────────────

    [Fact]
    public void AStandardCaseIsOpenToWhoeverHoldsTheModule()
    {
        ErCase standard = Of(investigatorId: Investigator, ownerId: Owner);

        Assert.True(CaseAccess.CanSee(standard, Stranger, holdsModule: true));
    }

    [Fact]
    public void NobodyWithoutTheModuleSeesACaseAtAll()
    {
        Assert.False(CaseAccess.CanSee(Of(), Stranger, holdsModule: false));
    }

    // Named individuals only, which is what the level means.
    [Fact]
    public void ARestrictedCaseIsOnlyForThePeopleNamedOnIt()
    {
        ErCase restricted = Of(
            confidentiality: ConfidentialityLevel.Restricted,
            investigatorId: Investigator,
            ownerId: Owner);

        Assert.True(CaseAccess.CanSee(restricted, Investigator, holdsModule: true));
        Assert.True(CaseAccess.CanSee(restricted, Owner, holdsModule: true));

        // Holding the module is not enough.
        Assert.False(CaseAccess.CanSee(restricted, Stranger, holdsModule: true));
    }

    [Fact]
    public void ARestrictedCaseIsNotEvenForItsSubject()
    {
        // A restricted case is typically about the person, not for them.
        ErCase restricted = Of(
            confidentiality: ConfidentialityLevel.Restricted,
            investigatorId: Investigator);

        Assert.False(CaseAccess.CanSee(restricted, Subject, holdsModule: true));
    }

    [Fact]
    public void TheOwnerAlwaysSeesTheirOwnRestrictedCase()
    {
        ErCase restricted = Of(
            confidentiality: ConfidentialityLevel.Restricted, ownerId: Hr);

        Assert.True(CaseAccess.CanSee(restricted, Hr, holdsModule: true));
    }

    // ── who can read a note ─────────────────────────────────────────────────

    [Fact]
    public void AnEmployeeVisibleNoteReachesThePersonTheCaseIsAbout()
    {
        Assert.True(
            CaseAccess.CanSeeNote(
                NoteVisibility.EmployeeVisible, isSubject: true, isCaseTeam: false));
    }

    [Fact]
    public void ACaseTeamNoteDoesNotReachThePersonTheCaseIsAbout()
    {
        // The middle ground a single internal flag could not express: the team
        // sees it, the employee does not.
        Assert.True(
            CaseAccess.CanSeeNote(
                NoteVisibility.CaseTeam, isSubject: false, isCaseTeam: true));

        Assert.False(
            CaseAccess.CanSeeNote(
                NoteVisibility.CaseTeam, isSubject: true, isCaseTeam: false));
    }

    [Fact]
    public void AnHrOnlyNoteReachesNeither()
    {
        Assert.False(
            CaseAccess.CanSeeNote(
                NoteVisibility.HrOnly, isSubject: true, isCaseTeam: false));

        Assert.False(
            CaseAccess.CanSeeNote(
                NoteVisibility.HrOnly, isSubject: false, isCaseTeam: true));
    }

    // ── notes and meetings ──────────────────────────────────────────────────

    [Fact]
    public void RefusesAnEmptyNote()
    {
        Assert.Equal(CaseRefusal.NoteRequired, CaseRules.NoteRefusal("   "));
    }

    [Fact]
    public void AcceptsANote()
    {
        Assert.Equal(CaseRefusal.None, CaseRules.NoteRefusal("Spoke to the line manager."));
    }

    [Fact]
    public void RefusesAMeetingWithNobodyAtIt()
    {
        CaseMeeting empty = new()
        {
            MeetingKind = CaseMeetingKind.Investigation,
            HeldOn = Today,
            Attendees = [],
        };

        Assert.Equal(CaseRefusal.AttendeesRequired, CaseRules.MeetingRefusal(empty, Today));
    }

    [Fact]
    public void AcceptsAMeetingSomebodyAttended()
    {
        CaseMeeting held = new()
        {
            MeetingKind = CaseMeetingKind.DisciplinaryHearing,
            HeldOn = Today,
            Attendees = ["Ada Okafor", "Bola Okafor"],
        };

        Assert.Equal(CaseRefusal.None, CaseRules.MeetingRefusal(held, Today));
    }

    // A hearing minuted for next month has not happened, so there is nothing to
    // record about it yet.
    [Fact]
    public void RefusesAMeetingRecordedBeforeItHappened()
    {
        CaseMeeting future = new()
        {
            MeetingKind = CaseMeetingKind.AppealHearing,
            HeldOn = Today.AddDays(7),
            Attendees = ["Ada Okafor"],
        };

        Assert.Equal(CaseRefusal.MeetingInTheFuture, CaseRules.MeetingRefusal(future, Today));
    }

    // ── closing one ─────────────────────────────────────────────────────────

    [Fact]
    public void AClosedCaseKeepsTheDayItClosed()
    {
        ErCase closing = Of(stage: CaseStage.OutcomeIssued);

        CaseRules.Moved(closing, CaseStage.Closed, Today);

        Assert.Equal(CaseStage.Closed, closing.Stage);
        Assert.Equal(Today, closing.ClosedOn);
    }

    [Fact]
    public void ReopeningACaseClearsTheClosureDate()
    {
        ErCase closed = Of(stage: CaseStage.Closed, closedOn: Today.AddDays(-2));

        CaseRules.Moved(closed, CaseStage.Appeal, Today);

        Assert.Null(closed.ClosedOn);
        Assert.Equal(CaseStage.Appeal, closed.Stage);
    }

    [Fact]
    public void MovingToAppealMarksTheCaseAsAppealed()
    {
        ErCase live = Of(stage: CaseStage.OutcomeIssued);

        CaseRules.Moved(live, CaseStage.Appeal, Today);

        Assert.True(live.HasAppeal);
    }
}
