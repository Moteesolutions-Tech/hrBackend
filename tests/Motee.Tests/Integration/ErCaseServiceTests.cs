using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Files;
using Motee.Application.Organisation;
using Motee.Application.Relations;
using Motee.Domain.Employees;
using Motee.Domain.Files;
using Motee.Domain.Organisation;
using Motee.Domain.Relations;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Employee-relations cases, out of the database.
//
// Four things worth proving beyond the plumbing. The case number is minted here
// rather than from the length of a list. A stage cannot be left until what it
// exists to record has been recorded, and the refusal says what is missing.
// Confidentiality is enforced, so a restricted case reaches the people named on
// it and nobody else. And the activity log comes off the audit trail, so the
// case's history cannot disagree with the record of what changed.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class ErCaseServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero));

    private static readonly DateOnly Today = new(2026, 6, 15);

    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _adaId;
    private Guid _bolaId;
    private Guid _chidiId;
    private Guid _adaUserId;

    // ── raising one ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task RaisesACase()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        CaseResult result = await Cases(provider).RaiseAsync(Raising());

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        CaseRowDto row = result.Case!.Case;

        Assert.Equal("ERC-001", row.CaseNumber);
        Assert.Equal(CaseStage.Raised, row.Stage);
        Assert.Equal("Bola Okafor", row.SubjectName);
        Assert.Equal("Engineering", row.SubjectDepartmentName);

        // Whoever is signed in raised it.
        Assert.Equal(_adaId, row.RaisedByEmployeeId);
        Assert.Equal(Today, row.RaisedOn);
        Assert.Equal(SlaState.None, row.SlaState);
        Assert.Equal(0, row.DaysOpen);
    }

    // Minted from the highest so far, not from the length of a list — which
    // hands two people raising a case at the same moment the same number.
    [SkippableFact]
    public async Task NumbersEachCaseAfterTheLast()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Cases(provider).RaiseAsync(Raising());
        await Cases(provider).RaiseAsync(Raising());

        CaseResult third = await Cases(provider).RaiseAsync(Raising());

        Assert.Equal("ERC-003", third.Case!.Case.CaseNumber);
    }

    // And deleting one does not hand its number to the next case.
    [SkippableFact]
    public async Task ADeletedCaseDoesNotFreeItsNumber()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        CaseResult first = await Cases(provider).RaiseAsync(Raising());
        CaseResult second = await Cases(provider).RaiseAsync(Raising());

        Assert.True((await Cases(provider).DeleteAsync(second.Case!.Case.Id)).Succeeded);

        CaseResult third = await Cases(provider).RaiseAsync(Raising());

        Assert.Equal("ERC-002", third.Case!.Case.CaseNumber);
        Assert.NotEqual(first.Case!.Case.CaseNumber, third.Case.Case.CaseNumber);
    }

    [SkippableFact]
    public async Task RefusesACaseWithNothingToGoOn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            CaseRefusal.DescriptionTooShort,
            (await Cases(provider).RaiseAsync(Raising(description: "Late"))).Refusal);
    }

    [SkippableFact]
    public async Task RefusesACaseAboutSomebodyWhoDoesNotWorkHere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            CaseOutcomeKind.EmployeeNotFound,
            (await Cases(provider).RaiseAsync(Raising(subject: Guid.NewGuid()))).Outcome);
    }

    [SkippableFact]
    public async Task RefusesAnInvestigatorWhoIsTheSubject()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        // Investigating yourself is not an investigation.
        Assert.Equal(
            CaseRefusal.InvestigatorIsSubject,
            (await Cases(provider).RaiseAsync(
                Raising() with { InvestigatorEmployeeId = _bolaId })).Refusal);
    }

    [SkippableFact]
    public async Task RefusesAnIncidentDatedInTheFuture()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            CaseRefusal.IncidentInTheFuture,
            (await Cases(provider).RaiseAsync(
                Raising() with { IncidentOn = Today.AddDays(1) })).Refusal);
    }

    // ── moving through the stages ───────────────────────────────────────────

    [SkippableFact]
    public async Task ACaseCannotSkipToTheEndAndSaysWhatIsMissing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await RaisedAsync(provider);

        CaseResult blocked = await Cases(provider).MoveAsync(id, CaseStage.Closed);

        Assert.Equal(CaseOutcomeKind.Blocked, blocked.Outcome);

        Assert.Contains("Set a target resolution date", blocked.Blockers);
        Assert.Contains("Assign an investigator", blocked.Blockers);
        Assert.Contains("Record the outcome", blocked.Blockers);
    }

    [SkippableFact]
    public async Task ACaseMovesOnOnceTheStageIsSatisfied()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await RaisedAsync(provider);

        await Cases(provider).UpdateAsync(
            id, Raising() with { TargetResolutionOn = Today.AddDays(30) });

        CaseResult moved = await Cases(provider).MoveAsync(id, CaseStage.Triage);

        Assert.True(moved.Succeeded, $"{moved.Outcome} / {string.Join("; ", moved.Blockers)}");
        Assert.Equal(CaseStage.Triage, moved.Case!.Case.Stage);
    }

    // Evidence satisfies the investigation gate, and it is counted from the
    // rows rather than from a figure somebody typed.
    [SkippableFact]
    public async Task AttachedEvidenceUnlocksTheInvestigationStage()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await AssignedAsync(provider);

        CaseResult blocked = await Cases(provider).MoveAsync(id, CaseStage.Hearing);

        Assert.Contains(
            "Record at least one witness statement or piece of evidence", blocked.Blockers);

        CaseResult attached = await Cases(provider).AttachEvidenceAsync(id, Attaching());

        Assert.True(attached.Succeeded, $"{attached.Outcome} / {attached.Rejection}");
        Assert.Equal(1, attached.Case!.Case.EvidenceCount);

        CaseResult now = await Cases(provider).MoveAsync(id, CaseStage.Investigation);

        Assert.True(now.Succeeded, string.Join("; ", now.Blockers));
    }

    [SkippableFact]
    public async Task ACaseCannotMoveToAStageItsKindDoesNotHave()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        CaseResult raised = await Cases(provider).RaiseAsync(
            Raising(type: CaseComplaintType.PerformanceImprovement));

        CaseResult blocked = await Cases(provider).MoveAsync(
            raised.Case!.Case.Id, CaseStage.Hearing);

        Assert.Equal(
            ["This kind of case has no hearing stage"], blocked.Blockers);
    }

    [SkippableFact]
    public async Task ACaseOffersOnlyTheStagesItsKindWalks()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        CaseResult raised = await Cases(provider).RaiseAsync(
            Raising(type: CaseComplaintType.PerformanceImprovement));

        CaseDto row = raised.Case!;

        Assert.DoesNotContain(CaseStage.Hearing, row.Flow);
        Assert.DoesNotContain(CaseStage.Hearing, row.Stages.Select(stage => stage.Stage));

        // And the tracker is told what is missing for each one it does have.
        // Entering triage is free — the description is all raising it needed.
        StageOptionDto triage = row.Stages.First(stage => stage.Stage == CaseStage.Triage);

        Assert.True(triage.Allowed);

        // Leaving it is what needs the target date, so that is where the
        // blocker appears.
        StageOptionDto assigned = row.Stages.First(stage => stage.Stage == CaseStage.Assigned);

        Assert.False(assigned.Allowed);
        Assert.Equal(["Set a target resolution date"], assigned.Blockers);
    }

    [SkippableFact]
    public async Task ClosingACaseStopsTheClock()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await ReadyToCloseAsync(provider);

        CaseResult closed = await Cases(provider).MoveAsync(id, CaseStage.Closed);

        Assert.True(closed.Succeeded, string.Join("; ", closed.Blockers));
        Assert.Equal(Today, closed.Case!.Case.ClosedOn);

        int daysAtClosure = closed.Case.Case.DaysOpen;

        // A month later the figure has not moved: nobody is working on it.
        _clock.Advance(TimeSpan.FromDays(30));

        CaseDto? later = await Cases(provider).GetAsync(id);

        Assert.Equal(daysAtClosure, later!.Case.DaysOpen);
    }

    [SkippableFact]
    public async Task ReopeningACaseClearsTheClosureDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await ReadyToCloseAsync(provider);

        await Cases(provider).MoveAsync(id, CaseStage.Closed);

        CaseResult reopened = await Cases(provider).MoveAsync(id, CaseStage.Appeal);

        Assert.True(reopened.Succeeded, string.Join("; ", reopened.Blockers));
        Assert.Null(reopened.Case!.Case.ClosedOn);
        Assert.True(reopened.Case.Case.HasAppeal);
    }

    // ── where it stands ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ACasePastItsTargetIsOverdue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await RaisedAsync(provider);

        await Cases(provider).UpdateAsync(
            id, Raising() with { TargetResolutionOn = Today.AddDays(2) });

        Assert.Equal(SlaState.DueSoon, (await Cases(provider).GetAsync(id))!.Case.SlaState);

        // Nothing is written. The same row reads differently a week later.
        _clock.Advance(TimeSpan.FromDays(7));

        Assert.Equal(SlaState.Overdue, (await Cases(provider).GetAsync(id))!.Case.SlaState);

        Assert.Equal(1, (await Cases(provider).SummaryAsync()).Overdue);
    }

    // The client reports every closed case as on track. A case that closed
    // twenty days late was not on track.
    [SkippableFact]
    public async Task ACaseThatClosedLateIsNotReportedAsOnTrack()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await ReadyToCloseAsync(provider, targetIn: 2);

        _clock.Advance(TimeSpan.FromDays(20));

        await Cases(provider).MoveAsync(id, CaseStage.Closed);

        Assert.Equal(SlaState.Overdue, (await Cases(provider).GetAsync(id))!.Case.SlaState);
    }

    // ── confidentiality ─────────────────────────────────────────────────────

    // Named individuals only, which is what the level means. The client shows
    // it as a badge and then lists every case to anybody holding the module.
    [SkippableFact]
    public async Task ARestrictedCaseIsOnlyForThePeopleNamedOnIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        CaseResult restricted = await Cases(provider).RaiseAsync(
            Raising() with
            {
                ConfidentialityLevel = ConfidentialityLevel.Restricted,
                InvestigatorEmployeeId = _chidiId,
                CaseOwnerEmployeeId = _chidiId,
            });

        Guid id = restricted.Case!.Case.Id;

        // Ada raised it but is not named on it, so it is not hers to read.
        Assert.Null(await Cases(provider).GetAsync(id));
        Assert.Empty((await Cases(provider).ListAsync(new CaseQuery())).Items);

        // Chidi is the investigator.
        ActAs(provider, await UserForAsync(_chidiId));

        Assert.NotNull(await Cases(provider).GetAsync(id));
        Assert.Single((await Cases(provider).ListAsync(new CaseQuery())).Items);
    }

    [SkippableFact]
    public async Task AStandardCaseIsOpenToWhoeverHoldsTheModule()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await RaisedAsync(provider);

        ActAs(provider, await UserForAsync(_chidiId));

        Assert.NotNull(await Cases(provider).GetAsync(id));
    }

    // ── the employee's own view ─────────────────────────────────────────────

    [SkippableFact]
    public async Task SomebodySeesTheCaseTheyRaisedAndTheOnesAboutThem()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        // Ada raises one about Bola.
        await Cases(provider).RaiseAsync(Raising());

        // And Chidi raises one about Ada.
        ActAs(provider, await UserForAsync(_chidiId));

        await Cases(provider).RaiseAsync(Raising(subject: _adaId));

        ActAs(provider, _adaUserId);

        PagedResult<CaseRowDto> mine = await Cases(provider).MineAsync(new CaseQuery());

        // Both: the one she raised, and the one about her.
        Assert.Equal(2, mine.TotalItems);
    }

    [SkippableFact]
    public async Task SomebodyDoesNotSeeACaseThatIsNothingToDoWithThem()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Cases(provider).RaiseAsync(Raising());

        ActAs(provider, await UserForAsync(_chidiId));

        Assert.Empty((await Cases(provider).MineAsync(new CaseQuery())).Items);
    }

    // A restricted case about somebody is not for them — but one they raised
    // themselves always is.
    [SkippableFact]
    public async Task ARestrictedCaseAboutSomebodyIsNotTheirsToRead()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        ActAs(provider, await UserForAsync(_chidiId));

        await Cases(provider).RaiseAsync(
            Raising(subject: _adaId) with
            {
                ConfidentialityLevel = ConfidentialityLevel.Restricted,
            });

        ActAs(provider, _adaUserId);

        Assert.Empty((await Cases(provider).MineAsync(new CaseQuery())).Items);
    }

    // ── notes ───────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ANoteDefaultsToTheTightestVisibility()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await RaisedAsync(provider);

        CaseResult result = await Cases(provider).AddNoteAsync(
            id, new NoteRequest { Content = "Spoke to the line manager." });

        CaseNoteDto note = Assert.Single(result.Case!.Notes);

        // A note written without a thought about who sees it should not be the
        // one that reaches the person the case is about.
        Assert.Equal(NoteVisibility.HrOnly, note.Visibility);
        Assert.Equal("Ada Okafor", note.AuthorName);
    }

    [SkippableFact]
    public async Task RefusesAnEmptyNote()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await RaisedAsync(provider);

        Assert.Equal(
            CaseRefusal.NoteRequired,
            (await Cases(provider).AddNoteAsync(id, new NoteRequest { Content = "   " }))
                .Refusal);
    }

    // The middle ground a single internal flag could not express.
    [SkippableFact]
    public async Task AnHrOnlyNoteNeverReachesThePersonTheCaseIsAbout()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        // A case about Ada, so she is the subject.
        CaseResult raised = await Cases(provider).RaiseAsync(
            Raising(subject: _adaId) with { CaseOwnerEmployeeId = _chidiId });

        Guid id = raised.Case!.Case.Id;

        await Cases(provider).AddNoteAsync(
            id,
            new NoteRequest
            {
                Content = "Previous warning on file.",
                Visibility = NoteVisibility.HrOnly,
            });

        await Cases(provider).AddNoteAsync(
            id,
            new NoteRequest
            {
                Content = "Hearing is on the 20th.",
                Visibility = NoteVisibility.EmployeeVisible,
            });

        // Ada is the subject and not on the case team, so she sees one of them.
        CaseDto? hers = await Cases(provider).GetAsync(id);

        Assert.Equal(
            ["Hearing is on the 20th."], hers!.Notes.Select(note => note.Content));

        // Chidi owns the case and sees both.
        ActAs(provider, await UserForAsync(_chidiId));

        CaseDto? theirs = await Cases(provider).GetAsync(id);

        Assert.Equal(2, theirs!.Notes.Count);
    }

    [SkippableFact]
    public async Task RemovesANote()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await RaisedAsync(provider);

        CaseResult added = await Cases(provider).AddNoteAsync(
            id, new NoteRequest { Content = "Spoke to the line manager." });

        CaseResult removed = await Cases(provider).RemoveNoteAsync(
            id, added.Case!.Notes[0].Id);

        Assert.True(removed.Succeeded, $"{removed.Outcome}");
        Assert.Empty(removed.Case!.Notes);
    }

    // ── meetings ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task RecordsAMeetingAndTakesTheHearingDateFromIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await RaisedAsync(provider);

        CaseResult result = await Cases(provider).RecordMeetingAsync(
            id,
            new MeetingRequest
            {
                MeetingKind = CaseMeetingKind.DisciplinaryHearing,
                HeldOn = Today.AddDays(-1),
                Attendees = ["Ada Okafor", "Bola Okafor", "  ", "Ada Okafor"],
                Notes = "Both accounts heard.",
            });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        CaseMeetingDto meeting = Assert.Single(result.Case!.Meetings);

        // Blanks dropped and the duplicate folded.
        Assert.Equal(["Ada Okafor", "Bola Okafor"], meeting.Attendees);

        // The hearing date is the meeting, so it is not typed twice.
        Assert.Equal(Today.AddDays(-1), result.Case.HearingOn);
    }

    [SkippableFact]
    public async Task RefusesAMeetingWithNobodyAtIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await RaisedAsync(provider);

        Assert.Equal(
            CaseRefusal.AttendeesRequired,
            (await Cases(provider).RecordMeetingAsync(
                id,
                new MeetingRequest
                {
                    MeetingKind = CaseMeetingKind.Investigation,
                    HeldOn = Today,
                    Attendees = [],
                })).Refusal);
    }

    [SkippableFact]
    public async Task RefusesAMeetingRecordedBeforeItHappened()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await RaisedAsync(provider);

        Assert.Equal(
            CaseRefusal.MeetingInTheFuture,
            (await Cases(provider).RecordMeetingAsync(
                id,
                new MeetingRequest
                {
                    MeetingKind = CaseMeetingKind.AppealHearing,
                    HeldOn = Today.AddDays(7),
                    Attendees = ["Ada Okafor"],
                })).Refusal);
    }

    // ── evidence ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AttachesEvidenceAndHandsItBackToDownload()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await RaisedAsync(provider);

        CaseResult attached = await Cases(provider).AttachEvidenceAsync(
            id, Attaching(name: "Rota for the week"));

        CaseEvidenceDto evidence = Assert.Single(attached.Case!.Evidence);

        Assert.Equal("Rota for the week", evidence.Name);
        Assert.Equal("rota.pdf", evidence.FileName);
        Assert.True(evidence.SizeBytes > 0);

        FileContent? content = await Cases(provider).OpenEvidenceAsync(id, evidence.Id);

        Assert.NotNull(content);
        Assert.Equal("rota.pdf", content.FileName);
    }

    [SkippableFact]
    public async Task TurnsAwayAFileTypeTheProductDoesNotAccept()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await RaisedAsync(provider);

        CaseResult result = await Cases(provider).AttachEvidenceAsync(
            id, Attaching(fileName: "proof.exe", contentType: "application/x-msdownload"));

        Assert.Equal(FileRejection.DisallowedType, result.Rejection);
    }

    [SkippableFact]
    public async Task RemovingEvidenceTakesTheBytesAndTheCountWithIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await RaisedAsync(provider);

        CaseResult attached = await Cases(provider).AttachEvidenceAsync(id, Attaching());

        Guid storedFileId = attached.Case!.Evidence[0].StoredFileId;

        CaseResult removed = await Cases(provider).RemoveEvidenceAsync(
            id, attached.Case.Evidence[0].Id);

        Assert.True(removed.Succeeded, $"{removed.Outcome}");
        Assert.Empty(removed.Case!.Evidence);
        Assert.Equal(0, removed.Case.Case.EvidenceCount);

        Assert.Null(await Resolve<IFileUploadService>(provider).GetAsync(storedFileId));
    }

    // ── the history ─────────────────────────────────────────────────────────

    // Read off the audit trail the change tracker already fills, merged with
    // the notes and meetings, so nothing is written twice.
    [SkippableFact]
    public async Task TheHistoryComesOffWhatWasAlreadyRecorded()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await RaisedAsync(provider);

        await Cases(provider).AddNoteAsync(
            id, new NoteRequest { Content = "Spoke to the line manager." });

        _clock.Advance(TimeSpan.FromHours(1));

        await Cases(provider).RecordMeetingAsync(
            id,
            new MeetingRequest
            {
                MeetingKind = CaseMeetingKind.Investigation,
                HeldOn = Today,
                Attendees = ["Ada Okafor"],
            });

        IReadOnlyList<CaseActivityDto> activity = await Cases(provider).ActivityAsync(id);

        // Newest first, so the latest thing to happen is at the top.
        Assert.Equal("MeetingRecorded", activity[0].Action);
        Assert.Contains("NoteAdded", activity.Select(item => item.Action));

        // And the change tracker's own entry for raising it.
        Assert.Contains(activity, item => item.Action == "Create");

        Assert.All(
            activity.Where(item => item.Action != "Create"),
            item => Assert.Equal("Ada Okafor", item.ActorName));

        // Nothing is stored for this: no row anywhere says "activity".
        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        Assert.Single(await read.CaseNotes.ToListAsync());
        Assert.Single(await read.CaseMeetings.ToListAsync());
    }

    // ── the list ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ListsTheOpenAndMostUrgentFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid closing = await ReadyToCloseAsync(provider);

        await Cases(provider).MoveAsync(closing, CaseStage.Closed);

        await Cases(provider).RaiseAsync(
            Raising(description: "A low-priority grumble about the kitchen rota.")
                with { Priority = CasePriority.Low });

        await Cases(provider).RaiseAsync(
            Raising(description: "An allegation that needs looking at today.")
                with { Priority = CasePriority.Urgent });

        PagedResult<CaseRowDto> listed = await Cases(provider).ListAsync(new CaseQuery());

        Assert.Equal(
            [CasePriority.Urgent, CasePriority.Low, CasePriority.Medium],
            listed.Items.Select(row => row.Priority));

        // The closed one is last whatever its priority.
        Assert.Equal(CaseStage.Closed, listed.Items[^1].Stage);
    }

    [SkippableFact]
    public async Task FindsACaseByItsNumber()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Cases(provider).RaiseAsync(Raising());
        await Cases(provider).RaiseAsync(Raising());

        Assert.Equal(
            "ERC-002",
            Assert.Single(
                (await Cases(provider).ListAsync(new CaseQuery { Search = "ERC-002" })).Items)
                .CaseNumber);
    }

    [SkippableFact]
    public async Task DoesNotTreatASearchTermAsAWildcard()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Cases(provider).RaiseAsync(
            Raising(description: "Claimed 100% attendance, which the rota contradicts."));

        await Cases(provider).RaiseAsync(Raising());

        Assert.Single(
            (await Cases(provider).ListAsync(new CaseQuery { Search = "100%" })).Items);
    }

    [SkippableFact]
    public async Task ReportsTheFiguresTheScreenLeadsWith()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid closing = await ReadyToCloseAsync(provider);

        _clock.Advance(TimeSpan.FromDays(4));

        await Cases(provider).MoveAsync(closing, CaseStage.Closed);
        await Cases(provider).RaiseAsync(Raising());

        CaseSummaryDto summary = await Cases(provider).SummaryAsync();

        Assert.Equal(2, summary.Total);
        Assert.Equal(1, summary.Open);
        Assert.Equal(1, summary.Unassigned);

        // Averaged over the cases that have closed, which is the only set where
        // the figure means anything.
        Assert.Equal(4, summary.AverageDaysToClose);

        Assert.Equal(1, summary.ByStage[CaseStage.Closed]);
        Assert.Equal(2, summary.ByType[CaseComplaintType.Disciplinary]);
    }

    [SkippableFact]
    public async Task WithNothingClosedThereIsNoAverageToReport()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Cases(provider).RaiseAsync(Raising());

        Assert.Null((await Cases(provider).SummaryAsync()).AverageDaysToClose);
    }

    // ── tenancy ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanyNeverSeesAnothersCases()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Cases(provider).RaiseAsync(Raising());

        RegisterTenantResult other = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ola",
            LastName = "Bello",
            Email = "ola@other.com",
            CompanyName = "Other Limited",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(other.Succeeded, string.Join("; ", other.Errors));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId =
            other.TenantId;

        Assert.Empty((await Cases(provider).ListAsync(new CaseQuery())).Items);

        // And the number starts again, because it is minted per company.
        Assert.Equal(0, (await Cases(provider).SummaryAsync()).Total);
    }

    // ── arrange ─────────────────────────────────────────────────────────────

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Owner",
            LastName = "Okafor",
            Email = "owner@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _adaId = await HireAsync(provider, "Ada");
        _bolaId = await HireAsync(provider, "Bola");
        _chidiId = await HireAsync(provider, "Chidi");

        _adaUserId = await AccountAsync(_adaId, "ada");
        await AccountAsync(_bolaId, "bola");
        await AccountAsync(_chidiId, "chidi");

        ActAs(provider, _adaUserId);

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IErCaseService Cases(ServiceProvider provider) =>
        Resolve<IErCaseService>(provider);

    private static void ActAs(ServiceProvider provider, Guid userId) =>
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();

    private CaseRequest Raising(
        CaseComplaintType type = CaseComplaintType.Disciplinary,
        string description = "Repeated lateness over six weeks, unexplained.",
        Guid? subject = null) => new()
        {
            ComplaintType = type,
            SubjectEmployeeId = subject ?? _bolaId,
            Description = description,
            Priority = CasePriority.Medium,
            ConfidentialityLevel = ConfidentialityLevel.Standard,
        };

    private async Task<Guid> RaisedAsync(ServiceProvider provider)
    {
        CaseResult result = await Cases(provider).RaiseAsync(Raising());

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Case!.Case.Id;
    }

    // Raised, triaged and assigned, which is as far as a case gets without
    // evidence.
    private async Task<Guid> AssignedAsync(ServiceProvider provider, int targetIn = 30)
    {
        Guid id = await RaisedAsync(provider);

        await Cases(provider).UpdateAsync(
            id,
            Raising() with
            {
                TargetResolutionOn = Today.AddDays(targetIn),
                InvestigatorEmployeeId = _chidiId,
                CaseOwnerEmployeeId = _adaId,
            });

        await Cases(provider).MoveAsync(id, CaseStage.Assigned);

        return id;
    }

    // Everything a disciplinary case needs to reach closure.
    private async Task<Guid> ReadyToCloseAsync(ServiceProvider provider, int targetIn = 30)
    {
        Guid id = await AssignedAsync(provider, targetIn);

        await Cases(provider).UpdateAsync(
            id,
            Raising() with
            {
                TargetResolutionOn = Today.AddDays(targetIn),
                InvestigatorEmployeeId = _chidiId,
                CaseOwnerEmployeeId = _adaId,
                Witnesses = [new CaseWitness { Name = "Ngozi", Statement = "Saw it." }],
                HearingOn = Today,
                HearingPanelEmployeeIds = [_adaId, _chidiId],
                Outcome = CaseOutcome.WrittenWarning,
                OutcomeOn = Today,
            });

        return id;
    }

    private async Task<Guid> HireAsync(ServiceProvider provider, string first) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;

    private async Task<Guid> AccountAsync(Guid employeeId, string handle)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Guid userId = Guid.NewGuid();

        seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
        {
            Id = userId,
            TenantId = _tenantId,
            EmployeeId = employeeId,
            FirstName = char.ToUpperInvariant(handle[0]) + handle[1..],
            LastName = "Okafor",
            Email = $"{handle}-login@acme.com",
            NormalizedEmail = $"{handle.ToUpperInvariant()}-LOGIN@ACME.COM",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();

        return userId;
    }

    private async Task<Guid> UserForAsync(Guid employeeId)
    {
        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        return await read.Users
            .IgnoreQueryFilters()
            .Where(user => user.EmployeeId == employeeId && user.TenantId == _tenantId)
            .Select(user => user.Id)
            .FirstAsync();
    }

    // The upload path reads the first bytes and refuses anything that
    // contradicts its declared type.
    private const string PdfHead = "%PDF-1.7\n";

    private static EvidenceUpload Attaching(
        string? name = "Rota for the week",
        string fileName = "rota.pdf",
        string contentType = "application/pdf",
        string body = "The rota, as published.") => new()
        {
            Name = name,
            Description = "Taken from the shared drive.",
            FileName = fileName,
            ContentType = contentType,
            Content = new MemoryStream(Encoding.UTF8.GetBytes(Bytes(contentType, body))),
            SizeBytes = Encoding.UTF8.GetByteCount(Bytes(contentType, body)),
        };

    private static string Bytes(string contentType, string body) =>
        contentType == "application/pdf" ? PdfHead + body : body;
}
