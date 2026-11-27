using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Health;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Health;
using Motee.Domain.Organisation;
using Motee.Domain.Sickness;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Occupational health, out of the database.
//
// Three things these are really about. The stage follows the absence until
// somebody refers, so a case cannot read "monitoring" with a ninety-day absence
// behind it. The candidates list finds absences past the threshold that nobody
// has referred — which the frontend cannot do, because its board is a list of
// referrals and an unreferred absence is invisible on it. And the manager view
// is a different shape rather than the same shape with fields stripped.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class OhServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero));

    private static readonly DateOnly Today = new(2026, 9, 22);

    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _adaId;
    private Guid _boluId;
    private Guid _hrId;

    // ── raising a case ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task RaisesACase()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        OhResult result = await Oh(provider).RaiseAsync(Raising());

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");
        Assert.Equal("OH-2026-0001", result.Case!.ReferenceNumber);
        Assert.Equal("Ada Okafor", result.Case.EmployeeName);
    }

    // Counted from what exists rather than from a list length, so removing a
    // case does not hand its number to the next one.
    [SkippableFact]
    public async Task ReferenceNumbersDoNotRepeatAfterARemoval()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid first = (await Oh(provider).RaiseAsync(Raising())).Case!.Id;

        await Oh(provider).MoveAsync(first, new OhStageRequest { Stage = OhStage.Closed });

        OhResult second = await Oh(provider).RaiseAsync(Raising(_boluId));

        Assert.Equal("OH-2026-0002", second.Case!.ReferenceNumber);
    }

    // The board would otherwise carry the same employee twice with two
    // different fitness opinions.
    [SkippableFact]
    public async Task RefusesASecondOpenCaseForTheSamePerson()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Oh(provider).RaiseAsync(Raising());

        Assert.Equal(OhRefusal.AlreadyOpen, (await Oh(provider).RaiseAsync(Raising())).Refusal);
    }

    [SkippableFact]
    public async Task ANewCaseCanBeOpenedOnceTheFirstIsClosed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid first = (await Oh(provider).RaiseAsync(Raising())).Case!.Id;

        await Oh(provider).MoveAsync(first, new OhStageRequest { Stage = OhStage.Closed });

        Assert.True((await Oh(provider).RaiseAsync(Raising())).Succeeded);
    }

    [SkippableFact]
    public async Task RefusesACaseForSomebodyWhoDoesNotWorkHere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            OhRefusal.EmployeeNotFound,
            (await Oh(provider).RaiseAsync(Raising(Guid.NewGuid()))).Refusal);
    }

    // Recording the referral and then having to move the stage would be two
    // steps for one decision.
    [SkippableFact]
    public async Task ACaseRaisedWithAReferralDateIsAlreadyReferred()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        OhResult result = await Oh(provider).RaiseAsync(
            Raising() with { ReferralDate = Today.AddDays(-2) });

        Assert.Equal(OhStage.Referred, result.Case!.Stage);
    }

    // ── the stage follows the absence ──────────────────────────────────────

    // The defect: the screen reads whatever was last stored, so a case can sit
    // at "monitoring duration" while the absence behind it has run for months.
    [SkippableFact]
    public async Task AnAbsencePastTheThresholdReadsAsThresholdReached()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        OhResult result = await Oh(provider).RaiseAsync(
            Raising() with { AbsenceStartDate = Today.AddDays(-60) });

        Assert.Equal(OhStage.ThresholdReached, result.Case!.Stage);

        // And what somebody actually recorded, so the screen can show that the
        // calendar moved it on.
        Assert.Equal(OhStage.AbsenceRecorded, result.Case.RecordedStage);
    }

    [SkippableFact]
    public async Task AShortAbsenceIsStillBeingMonitored()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        OhResult result = await Oh(provider).RaiseAsync(
            Raising() with { AbsenceStartDate = Today.AddDays(-10) });

        Assert.Equal(OhStage.Monitoring, result.Case!.Stage);
    }

    [SkippableFact]
    public async Task TheAbsenceLengthIsCounted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        OhResult result = await Oh(provider).RaiseAsync(
            Raising() with { AbsenceStartDate = Today.AddDays(-45) });

        Assert.Equal(45, result.Case!.AbsenceDays);
    }

    // Without this the longest absences on the board are all people who came
    // back years ago.
    [SkippableFact]
    public async Task AClosedCasesAbsenceStopsCounting()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Oh(provider).RaiseAsync(
            Raising() with { AbsenceStartDate = Today.AddDays(-30) })).Case!.Id;

        await Oh(provider).MoveAsync(id, new OhStageRequest { Stage = OhStage.Closed });

        _clock.Advance(TimeSpan.FromDays(100));

        Assert.Equal(30, (await Oh(provider).CaseAsync(id))!.AbsenceDays);
    }

    // ── moving a case on ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task RefusingToReferWithoutAReferralDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Oh(provider).RaiseAsync(Raising())).Case!.Id;

        Assert.Equal(
            OhRefusal.ReferralDateRequired,
            (await Oh(provider).MoveAsync(
                id, new OhStageRequest { Stage = OhStage.Referred })).Refusal);
    }

    // A clinician's advice is the fitness opinion. Reaching this stage with it
    // pending means the advice has not arrived.
    [SkippableFact]
    public async Task RecommendationsNeedAFitnessOpinion()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await AssessedAsync(provider);

        Assert.Equal(
            OhRefusal.FitnessRequired,
            (await Oh(provider).MoveAsync(
                id, new OhStageRequest { Stage = OhStage.RecommendationsReceived })).Refusal);
    }

    [SkippableFact]
    public async Task WithAFitnessOpinionTheCaseMovesOn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await AssessedAsync(provider, OhFitness.FitWithAdjustments);

        OhResult moved = await Oh(provider).MoveAsync(
            id, new OhStageRequest { Stage = OhStage.RecommendationsReceived });

        Assert.True(moved.Succeeded, $"{moved.Outcome} / {moved.Refusal}");
        Assert.Equal(OhStage.RecommendationsReceived, moved.Case!.Stage);
    }

    // A case claiming implemented adjustments with none recorded is the board
    // reporting work that does not exist.
    [SkippableFact]
    public async Task ImplementedAdjustmentsNeedAdjustments()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await AtHrReviewAsync(provider);

        Assert.Equal(
            OhRefusal.DescriptionRequired,
            (await Oh(provider).MoveAsync(
                id, new OhStageRequest { Stage = OhStage.AdjustmentsImplemented })).Refusal);
    }

    [SkippableFact]
    public async Task ACaseDoesNotGoBackwards()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await AtHrReviewAsync(provider);

        Assert.Equal(
            OhRefusal.StageNotReachable,
            (await Oh(provider).MoveAsync(
                id, new OhStageRequest { Stage = OhStage.Referred })).Refusal);
    }

    [SkippableFact]
    public async Task ACaseCanBeClosedFromAnywhereAndStampsTheDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Oh(provider).RaiseAsync(Raising())).Case!.Id;

        OhResult closed = await Oh(provider).MoveAsync(
            id, new OhStageRequest { Stage = OhStage.Closed });

        Assert.True(closed.Succeeded, $"{closed.Outcome} / {closed.Refusal}");
        Assert.Equal(Today, closed.Case!.ClosedOn);
        Assert.Null(closed.Case.NextAction);
    }

    // What reopens a closed case is new information for HR to weigh.
    [SkippableFact]
    public async Task ReopeningLandsAtHrReviewAndClearsTheClosedDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Oh(provider).RaiseAsync(Raising())).Case!.Id;

        await Oh(provider).MoveAsync(id, new OhStageRequest { Stage = OhStage.Closed });

        OhResult reopened = await Oh(provider).MoveAsync(
            id, new OhStageRequest { Stage = OhStage.HrReview });

        Assert.True(reopened.Succeeded, $"{reopened.Outcome} / {reopened.Refusal}");
        Assert.Null(reopened.Case!.ClosedOn);
    }

    // Derived from the stage, so it cannot keep saying "chase the appointment"
    // after the appointment happened.
    [SkippableFact]
    public async Task TheNextActionFollowsTheStage()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await ReferredAsync(provider);

        Assert.Contains("appointment", (await Oh(provider).CaseAsync(id))!.NextAction!.Label);

        Guid assessed = await AssessedAsync(provider, employeeId: _boluId);

        Assert.Contains("report", (await Oh(provider).CaseAsync(assessed))!.NextAction!.Label);
    }

    // ── adjustments ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnAdjustmentIsProposed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await AtHrReviewAsync(provider);

        OhResult result = await Oh(provider).AddAdjustmentAsync(id, Adjusting());

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        AdjustmentDto adjustment = Assert.Single(result.Case!.Adjustments);

        Assert.Equal("Phased return over four weeks", adjustment.Description);
        Assert.Equal(AdjustmentStage.Proposed, adjustment.Stage);
        Assert.False(adjustment.IsInPlace);
    }

    // An agreed adjustment with no owner is the commitment that quietly never
    // happens.
    [SkippableFact]
    public async Task AgreeingAnAdjustmentNeedsAnOwner()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid id, Guid adjustmentId) = await ProposedAsync(provider);

        Assert.Equal(
            OhRefusal.OwnerNotFound,
            (await Oh(provider).MoveAdjustmentAsync(
                id,
                adjustmentId,
                new AdjustmentStageRequest { Stage = AdjustmentStage.Agreed })).Refusal);
    }

    [SkippableFact]
    public async Task WithAnOwnerTheAdjustmentIsAgreed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid id, Guid adjustmentId) = await ProposedAsync(provider, owner: true);

        OhResult agreed = await Oh(provider).MoveAdjustmentAsync(
            id, adjustmentId, new AdjustmentStageRequest { Stage = AdjustmentStage.Agreed });

        Assert.True(agreed.Succeeded, $"{agreed.Outcome} / {agreed.Refusal}");
        Assert.Equal("HR Okafor", agreed.Case!.Adjustments[0].ImplementationOwnerName);
    }

    // An adjustment in place without a date it started is one nobody can review
    // or evidence.
    [SkippableFact]
    public async Task ImplementingAnAdjustmentNeedsAStartDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid id, Guid adjustmentId) = await ProposedAsync(provider, owner: true);

        await Oh(provider).MoveAdjustmentAsync(
            id, adjustmentId, new AdjustmentStageRequest { Stage = AdjustmentStage.Agreed });

        Assert.Equal(
            OhRefusal.ReferralDateRequired,
            (await Oh(provider).MoveAdjustmentAsync(
                id,
                adjustmentId,
                new AdjustmentStageRequest { Stage = AdjustmentStage.Implemented })).Refusal);
    }

    [SkippableFact]
    public async Task AnImplementedAdjustmentIsInPlace()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid id, Guid adjustmentId) = await InPlaceAsync(provider);

        OhCaseDto? reloaded = await Oh(provider).CaseAsync(id);

        Assert.True(reloaded!.Adjustments.Single(a => a.Id == adjustmentId).IsInPlace);
    }

    // With adjustments in place the case can claim them.
    [SkippableFact]
    public async Task WithAdjustmentsInPlaceTheCaseCanMoveOn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid id, _) = await InPlaceAsync(provider);

        OhResult moved = await Oh(provider).MoveAsync(
            id, new OhStageRequest { Stage = OhStage.AdjustmentsImplemented });

        Assert.True(moved.Succeeded, $"{moved.Outcome} / {moved.Refusal}");
    }

    // A case whose only adjustment was withdrawn has none in place, and showing
    // it on the Adjustments tab would report help that was withdrawn.
    [SkippableFact]
    public async Task AWithdrawnAdjustmentDoesNotCount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid id, Guid adjustmentId) = await ProposedAsync(provider);

        await Oh(provider).MoveAdjustmentAsync(
            id, adjustmentId, new AdjustmentStageRequest { Stage = AdjustmentStage.Closed });

        PagedResult<OhCaseDto> board = await Oh(provider).BoardAsync(
            new OhCaseQuery { Tab = OhBoardTab.Adjustments });

        Assert.Equal(0, board.TotalItems);
    }

    [SkippableFact]
    public async Task AnAdjustmentIsRemoved()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid id, Guid adjustmentId) = await ProposedAsync(provider);

        Assert.Equal(
            OhOutcome.Succeeded,
            await Oh(provider).RemoveAdjustmentAsync(id, adjustmentId));

        Assert.Empty((await Oh(provider).CaseAsync(id))!.Adjustments);
    }

    // ── the candidates list ────────────────────────────────────────────────

    // The one thing the frontend cannot produce: its board is a list of
    // referrals, so an absence nobody has raised a referral for is invisible.
    [SkippableFact]
    public async Task AnAbsencePastTheThresholdWithNoReferralIsACandidate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await AbsenceAsync(_adaId, Today.AddDays(-40));

        OhCandidateDto candidate = Assert.Single(await Oh(provider).CandidatesAsync());

        Assert.Equal(_adaId, candidate.EmployeeId);
        Assert.Equal("Ada Okafor", candidate.EmployeeName);
        Assert.Equal(40, candidate.AbsenceDays);
        Assert.Equal(12, candidate.DaysPastThreshold);
    }

    [SkippableFact]
    public async Task AShortAbsenceIsNotACandidate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await AbsenceAsync(_adaId, Today.AddDays(-10));

        Assert.Empty(await Oh(provider).CandidatesAsync());
    }

    // Resurfacing it would ask HR to do work they have already done.
    [SkippableFact]
    public async Task SomebodyAlreadyOnTheBoardIsNotACandidate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await AbsenceAsync(_adaId, Today.AddDays(-40));
        await Oh(provider).RaiseAsync(Raising());

        Assert.Empty(await Oh(provider).CandidatesAsync());
    }

    // The threshold is about continuous absence, and the person is back.
    [SkippableFact]
    public async Task SomebodyWhoHasReturnedIsNotACandidate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await AbsenceAsync(_adaId, Today.AddDays(-40), returned: true);

        Assert.Empty(await Oh(provider).CandidatesAsync());
    }

    // The same rule the sickness figures use, so the two screens cannot
    // disagree about who has been off.
    [SkippableFact]
    public async Task ACancelledAbsenceIsNotACandidate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await AbsenceAsync(
            _adaId, Today.AddDays(-40), approval: SicknessApprovalStatus.Cancelled);

        Assert.Empty(await Oh(provider).CandidatesAsync());
    }

    // Waiting for approval still counts — the person was off whether or not a
    // manager has clicked yet.
    [SkippableFact]
    public async Task AnAbsenceAwaitingApprovalIsStillACandidate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await AbsenceAsync(
            _adaId, Today.AddDays(-40), approval: SicknessApprovalStatus.AwaitingApproval);

        Assert.Single(await Oh(provider).CandidatesAsync());
    }

    [SkippableFact]
    public async Task TheMostOverdueCandidateComesFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await AbsenceAsync(_adaId, Today.AddDays(-35));
        await AbsenceAsync(_boluId, Today.AddDays(-90));

        IReadOnlyList<OhCandidateDto> candidates = await Oh(provider).CandidatesAsync();

        Assert.Equal(_boluId, candidates[0].EmployeeId);
    }

    // ── the board ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheBoardCanBeNarrowedToWhatIsAwaitingTheProvider()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ReferredAsync(provider);
        await Oh(provider).RaiseAsync(Raising(_boluId));

        PagedResult<OhCaseDto> awaiting = await Oh(provider).BoardAsync(
            new OhCaseQuery { Tab = OhBoardTab.AwaitingProvider });

        Assert.Equal(1, awaiting.TotalItems);
        Assert.Equal(_adaId, awaiting.Items[0].EmployeeId);
    }

    [SkippableFact]
    public async Task TheLongestAbsenceComesFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Oh(provider).RaiseAsync(
            Raising() with { AbsenceStartDate = Today.AddDays(-10) });
        await Oh(provider).RaiseAsync(
            Raising(_boluId) with { AbsenceStartDate = Today.AddDays(-80) });

        PagedResult<OhCaseDto> board = await Oh(provider).BoardAsync(new OhCaseQuery());

        Assert.Equal(_boluId, board.Items[0].EmployeeId);
    }

    [SkippableFact]
    public async Task TheSummaryCountsWhatMatters()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Oh(provider).RaiseAsync(
            Raising() with { AbsenceStartDate = Today.AddDays(-60) });
        await ReferredAsync(provider, employeeId: _boluId);

        OhTally tally = await Oh(provider).SummaryAsync();

        Assert.Equal(2, tally.Open);
        Assert.Equal(1, tally.AwaitingProvider);

        // The figure this whole screen exists to keep at zero.
        Assert.Equal(1, tally.PastThresholdUnreferred);
    }

    [SkippableFact]
    public async Task ReviewsDueAreCounted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Oh(provider).RaiseAsync(Raising() with { ReviewDate = Today });
        await Oh(provider).RaiseAsync(
            Raising(_boluId) with { ReviewDate = Today.AddDays(30) });

        Assert.Equal(1, (await Oh(provider).SummaryAsync()).ReviewsDue);
    }

    // ── the manager view ───────────────────────────────────────────────────

    // A shape with nowhere to put the case notes, rather than a full record
    // with fields stripped on the way out.
    [SkippableFact]
    public async Task TheManagerViewCarriesFitnessAndAdjustmentsAndNothingElse()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid id, _) = await InPlaceAsync(provider);

        await Oh(provider).UpdateAsync(
            id,
            Raising() with
            {
                Fitness = OhFitness.FitWithAdjustments,
                CaseNotes = "Appointment moved to the 14th.",
                ReferralDate = Today.AddDays(-20),
                AssessmentDate = Today.AddDays(-10),
            });

        OhManagerView? narrow = await Oh(provider).ManagerCaseAsync(id);

        Assert.NotNull(narrow);
        Assert.Equal(OhFitness.FitWithAdjustments, narrow.Fitness);
        Assert.Single(narrow.Adjustments, "Phased return over four weeks");
        Assert.True(narrow.IsManagerView);

        // And the confidential view does carry them, so the difference is the
        // view rather than the data.
        Assert.Equal(
            "Appointment moved to the 14th.", (await Oh(provider).CaseAsync(id))!.CaseNotes);
    }

    // Withdrawn adjustments are not what somebody is managing around.
    [SkippableFact]
    public async Task TheManagerViewLeavesOutWithdrawnAdjustments()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid id, Guid adjustmentId) = await ProposedAsync(provider);

        await Oh(provider).MoveAdjustmentAsync(
            id, adjustmentId, new AdjustmentStageRequest { Stage = AdjustmentStage.Closed });

        Assert.Empty((await Oh(provider).ManagerCaseAsync(id))!.Adjustments);
    }

    // ── history ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheHistoryComesFromTheAuditTrail()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        // Through a real stage move. A case raised with a referral date is
        // already at Referred, so it has nothing but the opening entry — which
        // is right, and is why this walks it forward instead.
        Guid id = await AssessedAsync(provider);

        IReadOnlyList<OhHistoryDto> history = await Oh(provider).HistoryAsync(id);

        Assert.Contains(history, entry => entry.Note.Contains("opened"));
        Assert.Contains(history, entry => entry.Note.Contains("assessment completed"));
        Assert.All(history, entry => Assert.Equal("tester@acme.com", entry.ActorName));
    }

    // Free text is where a diagnosis arrives however the field is labelled, so
    // the fact of a change is auditable and the content is not.
    [SkippableFact]
    public async Task TheAuditTrailDoesNotReproduceCaseNotes()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Oh(provider).RaiseAsync(Raising())).Case!.Id;

        await Oh(provider).UpdateAsync(
            id, Raising() with { CaseNotes = "Something nobody else should read." });

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        List<string?> changes = await context.AuditEntries
            .Where(entry => entry.EntityType == nameof(OhReferral))
            .Select(entry => entry.Changes)
            .ToListAsync();

        Assert.All(
            changes,
            change => Assert.DoesNotContain(
                "nobody else should read", change ?? string.Empty, StringComparison.Ordinal));
    }

    // ── tenancy ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanysCasesAreNotAnothers()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Oh(provider).RaiseAsync(Raising());

        RegisterTenantResult other = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Other",
            LastName = "Owner",
            Email = "owner@other.com",
            CompanyName = "Other Limited",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = other.TenantId;

        Assert.Equal(0, (await Oh(provider).BoardAsync(new OhCaseQuery())).TotalItems);
    }

    // ── linked absences ────────────────────────────────────────────────────
    //
    // A case holds one triggering absence and now a set of linked ones. The
    // screen's Link Absence tab exists because a long-running case is usually
    // several spells rather than one — somebody off, back for a fortnight, off
    // again — and a single SicknessRecordId could only ever name the first.

    [SkippableFact]
    public async Task SeveralAbsencesCanBeLinkedToOneCase()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await ReferredAsync(provider);

        Guid first = await AbsenceIdAsync(_adaId, Today.AddDays(-60), returned: true);
        Guid second = await AbsenceIdAsync(_adaId, Today.AddDays(-30), returned: true);

        OhResult linked = await Oh(provider).SetAbsencesAsync(id, [first, second]);

        Assert.True(linked.Succeeded, $"{linked.Outcome} / {linked.Refusal}");

        // Both, which a single SicknessRecordId could never hold — and the whole
        // reason the frontend's Link Absence tab had nothing to call.
        Assert.Equal(
            [first, second],
            linked.Case!.LinkedAbsences.Select(absence => absence.Id).ToArray());

        // Still there on a fresh read, rather than only in the save's answer.
        OhCaseDto reread = (await Oh(provider).CaseAsync(id))!;

        Assert.Equal(2, reread.LinkedAbsences.Count);
    }

    [SkippableFact]
    public async Task LinkedAbsencesComeBackOldestFirstWithTheirDayCount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await ReferredAsync(provider);

        Guid recent = await AbsenceIdAsync(_adaId, Today.AddDays(-30), returned: true);
        Guid older = await AbsenceIdAsync(_adaId, Today.AddDays(-60), returned: true);

        OhResult linked = await Oh(provider).SetAbsencesAsync(id, [recent, older]);
        Assert.True(linked.Succeeded, $"{linked.Outcome} / {linked.Refusal}");

        IReadOnlyList<LinkedAbsenceDto> absences = linked.Case!.LinkedAbsences;

        // A history reads forwards. Oldest first is the order the case was built
        // up in, which is the order somebody reviewing it wants.
        Assert.Equal(older, absences[0].Id);
        Assert.Equal(recent, absences[1].Id);

        // AbsenceAsync ends a returned spell three days after it starts, so each
        // is a four-day absence counted inclusively.
        Assert.Equal(Today.AddDays(-60), absences[0].StartDate);
        Assert.Equal(Today.AddDays(-57), absences[0].EndDate);
        Assert.Equal(4, absences[0].Days);
    }

    // Set, not add: the screen hands over the list it is showing, and anything
    // missing from it has been unlinked.
    [SkippableFact]
    public async Task LinkingReplacesTheWholeList()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await ReferredAsync(provider);

        Guid first = await AbsenceIdAsync(_adaId, Today.AddDays(-60), returned: true);
        Guid second = await AbsenceIdAsync(_adaId, Today.AddDays(-30), returned: true);

        Assert.True((await Oh(provider).SetAbsencesAsync(id, [first, second])).Succeeded);

        OhResult reduced = await Oh(provider).SetAbsencesAsync(id, [second]);

        Assert.True(reduced.Succeeded, $"{reduced.Outcome} / {reduced.Refusal}");
        Assert.Equal(second, Assert.Single(reduced.Case!.LinkedAbsences).Id);
    }

    [SkippableFact]
    public async Task ClearingTheListLeavesNothingLinked()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await ReferredAsync(provider);
        Guid absence = await AbsenceIdAsync(_adaId, Today.AddDays(-60), returned: true);

        Assert.True((await Oh(provider).SetAbsencesAsync(id, [absence])).Succeeded);

        OhResult cleared = await Oh(provider).SetAbsencesAsync(id, []);

        Assert.True(cleared.Succeeded, $"{cleared.Outcome} / {cleared.Refusal}");
        Assert.Empty(cleared.Case!.LinkedAbsences);
    }

    // The rule that matters. Linking somebody else's absence to this case would
    // put one person's health record on another person's file.
    [SkippableFact]
    public async Task AnAbsenceBelongingToSomebodyElseCannotBeLinked()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await ReferredAsync(provider);

        Guid benId = await HireAsync(provider, "Ben");
        Guid bensAbsence = await AbsenceIdAsync(benId, Today.AddDays(-30), returned: true);

        OhResult refused = await Oh(provider).SetAbsencesAsync(id, [bensAbsence]);

        Assert.Equal(OhOutcome.Refused, refused.Outcome);

        // Told apart from an id that simply is not ours: they are different mistakes,
        // and "not found" would send whoever is fixing it looking in the wrong place.
        Assert.Equal(OhRefusal.AbsenceBelongsToSomebodyElse, refused.Refusal);
    }

    [SkippableFact]
    public async Task AnAbsenceThatIsNotThereCannotBeLinked()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await ReferredAsync(provider);

        OhResult refused = await Oh(provider).SetAbsencesAsync(id, [Guid.NewGuid()]);

        Assert.Equal(OhOutcome.Refused, refused.Outcome);
    }

    [SkippableFact]
    public async Task LinkingAgainstACaseThatIsNotThereIsNotFound()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        OhResult missing = await Oh(provider).SetAbsencesAsync(Guid.NewGuid(), []);

        Assert.Equal(OhOutcome.NotFound, missing.Outcome);
    }

    // The same absence twice is one link. The screen can send a list with a
    // duplicate in it and the case does not then show the spell twice.
    [SkippableFact]
    public async Task TheSameAbsenceTwiceIsLinkedOnce()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await ReferredAsync(provider);
        Guid absence = await AbsenceIdAsync(_adaId, Today.AddDays(-30), returned: true);

        OhResult linked = await Oh(provider).SetAbsencesAsync(id, [absence, absence]);

        Assert.True(linked.Succeeded, $"{linked.Outcome} / {linked.Refusal}");
        Assert.Single(linked.Case!.LinkedAbsences);
    }

    // Deleting the case takes its links with it rather than leaving rows
    // pointing at a referral that is gone.
    [SkippableFact]
    public async Task RemovingACaseRemovesItsLinks()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await ReferredAsync(provider);
        Guid absence = await AbsenceIdAsync(_adaId, Today.AddDays(-30), returned: true);

        Assert.True((await Oh(provider).SetAbsencesAsync(id, [absence])).Succeeded);
        Assert.Equal(OhOutcome.Succeeded, await Oh(provider).RemoveAsync(id));

        await using MoteeDbContext check = fixture.CreateContext(_tenantId);

        Assert.Empty(check.OhReferralAbsences);
    }

    // ── arrange ────────────────────────────────────────────────────────────

    private async Task<Guid> AbsenceIdAsync(
        Guid employeeId,
        DateOnly start,
        bool returned = false)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Guid id = Guid.NewGuid();

        seed.SicknessRecords.Add(new SicknessRecord
        {
            Id = id,
            TenantId = _tenantId,
            EmployeeId = employeeId,
            StartDate = start,
            EndDate = returned ? start.AddDays(3) : null,
            AbsenceState = returned ? AbsenceState.Returned : AbsenceState.Ongoing,
            ApprovalStatus = SicknessApprovalStatus.Approved,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
        });

        await seed.SaveChangesAsync();

        return id;
    }

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
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            registration.UserId.ToString();

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _adaId = await HireAsync(provider, "Ada");
        _boluId = await HireAsync(provider, "Bolu");
        _hrId = await HireAsync(provider, "HR");

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IOhService Oh(ServiceProvider provider) => Resolve<IOhService>(provider);

    private OhReferralRequest Raising(Guid? employeeId = null) => new()
    {
        EmployeeId = employeeId ?? _adaId,
        AbsenceStartDate = Today.AddDays(-14),
    };

    private static AdjustmentRequest Adjusting(Guid? owner = null) => new()
    {
        Description = "Phased return over four weeks",
        ImplementationOwnerEmployeeId = owner,
    };

    private async Task<Guid> ReferredAsync(ServiceProvider provider, Guid? employeeId = null)
    {
        OhResult raised = await Oh(provider).RaiseAsync(
            Raising(employeeId) with { ReferralDate = Today.AddDays(-5) });

        Assert.True(raised.Succeeded, $"{raised.Outcome} / {raised.Refusal}");

        return raised.Case!.Id;
    }

    private async Task<Guid> AssessedAsync(
        ServiceProvider provider,
        OhFitness fitness = OhFitness.Pending,
        Guid? employeeId = null)
    {
        Guid id = await ReferredAsync(provider, employeeId);

        OhResult updated = await Oh(provider).UpdateAsync(
            id,
            Raising(employeeId) with
            {
                ReferralDate = Today.AddDays(-5),
                AssessmentDate = Today.AddDays(-2),
                Fitness = fitness,
            });

        Assert.True(updated.Succeeded, $"{updated.Outcome} / {updated.Refusal}");

        OhResult moved = await Oh(provider).MoveAsync(
            id, new OhStageRequest { Stage = OhStage.AssessmentCompleted });

        Assert.True(moved.Succeeded, $"{moved.Outcome} / {moved.Refusal}");

        return id;
    }

    private async Task<Guid> AtHrReviewAsync(ServiceProvider provider)
    {
        Guid id = await AssessedAsync(provider, OhFitness.FitWithAdjustments);

        OhResult moved = await Oh(provider).MoveAsync(
            id, new OhStageRequest { Stage = OhStage.HrReview });

        Assert.True(moved.Succeeded, $"{moved.Outcome} / {moved.Refusal}");

        return id;
    }

    private async Task<(Guid Id, Guid AdjustmentId)> ProposedAsync(
        ServiceProvider provider,
        bool owner = false)
    {
        Guid id = await AtHrReviewAsync(provider);

        OhResult added = await Oh(provider).AddAdjustmentAsync(
            id, Adjusting(owner ? _hrId : null));

        Assert.True(added.Succeeded, $"{added.Outcome} / {added.Refusal}");

        return (id, added.Case!.Adjustments[0].Id);
    }

    private async Task<(Guid Id, Guid AdjustmentId)> InPlaceAsync(ServiceProvider provider)
    {
        (Guid id, Guid adjustmentId) = await ProposedAsync(provider, owner: true);

        await Oh(provider).MoveAdjustmentAsync(
            id, adjustmentId, new AdjustmentStageRequest { Stage = AdjustmentStage.Agreed });

        OhResult dated = await Oh(provider).UpdateAdjustmentAsync(
            id,
            adjustmentId,
            Adjusting(_hrId) with { EffectiveDate = Today.AddDays(-1) });

        Assert.True(dated.Succeeded, $"{dated.Outcome} / {dated.Refusal}");

        OhResult implemented = await Oh(provider).MoveAdjustmentAsync(
            id, adjustmentId, new AdjustmentStageRequest { Stage = AdjustmentStage.Implemented });

        Assert.True(implemented.Succeeded, $"{implemented.Outcome} / {implemented.Refusal}");

        return (id, adjustmentId);
    }

    // Written straight in: raising one through the sickness service would drag
    // its own approval chain into a test about occupational health.
    private async Task AbsenceAsync(
        Guid employeeId,
        DateOnly start,
        bool returned = false,
        SicknessApprovalStatus approval = SicknessApprovalStatus.Approved)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.SicknessRecords.Add(new SicknessRecord
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = employeeId,
            StartDate = start,
            EndDate = returned ? start.AddDays(3) : null,
            AbsenceState = returned ? AbsenceState.Returned : AbsenceState.Ongoing,
            ApprovalStatus = approval,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
        });

        await seed.SaveChangesAsync();
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
}
