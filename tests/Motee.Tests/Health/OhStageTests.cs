using Motee.Domain.Health;

namespace Motee.Tests.Health;

// Where an occupational health case has got to, and what happens next.
//
// Two things the screen gets wrong, both because it stores what it should
// derive. The stage is a plain field, so a case can read "Monitoring duration"
// with a ninety-day absence behind it — the one thing this board exists to stop.
// And nextAction sits beside the stage with nothing tying them together, so a
// case moved on from Referred keeps showing "Chase the OH appointment".
//
// Written before the rules.
public class OhStageTests
{
    private static readonly DateOnly Today = new(2026, 9, 22);

    private static OhReferral At(
        OhStage stage,
        DateOnly? absenceStart = null,
        DateOnly? referred = null,
        DateOnly? assessed = null,
        OhFitness fitness = OhFitness.Pending,
        DateOnly? closed = null) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            EmployeeId = Guid.NewGuid(),
            ReferenceNumber = "OH-0001",
            Stage = stage,
            AbsenceStartDate = absenceStart,
            ReferralDate = referred,
            AssessmentDate = assessed,
            Fitness = fitness,
            ClosedOn = closed,
        };

    // ── the stage follows the absence until somebody refers ────────────────

    [Fact]
    public void AShortAbsenceIsBeingMonitored()
    {
        Assert.Equal(
            OhStage.Monitoring,
            OhStages.Of(
                At(OhStage.AbsenceRecorded, absenceStart: Today.AddDays(-10)), Today));
    }

    // The defect: the screen would keep reading "Monitoring duration" because
    // that is what somebody last stored.
    [Fact]
    public void AnAbsencePastTheThresholdSaysSoWhateverWasStored()
    {
        Assert.Equal(
            OhStage.ThresholdReached,
            OhStages.Of(
                At(OhStage.Monitoring, absenceStart: Today.AddDays(-90)), Today));
    }

    [Fact]
    public void TheThresholdIsFourWeeks()
    {
        Assert.Equal(28, OhStages.ReferralThresholdDays);

        Assert.Equal(
            OhStage.ThresholdReached,
            OhStages.Of(At(OhStage.Monitoring, absenceStart: Today.AddDays(-28)), Today));

        Assert.Equal(
            OhStage.Monitoring,
            OhStages.Of(At(OhStage.Monitoring, absenceStart: Today.AddDays(-27)), Today));
    }

    // HrAlerted is a fact about a notification having gone out. The calendar
    // must not pull a case back to ThresholdReached once somebody has been told.
    [Fact]
    public void TheCalendarDoesNotUndoAnAlertAlreadySent()
    {
        Assert.Equal(
            OhStage.HrAlerted,
            OhStages.Of(At(OhStage.HrAlerted, absenceStart: Today.AddDays(-40)), Today));
    }

    // From the referral onwards each step is something somebody did, and no
    // date can tell us whether they did it.
    [Fact]
    public void OnceReferredTheStoredStageGoverns()
    {
        Assert.Equal(
            OhStage.Referred,
            OhStages.Of(At(OhStage.Referred, absenceStart: Today.AddDays(-200)), Today));
    }

    [Fact]
    public void WithNoAbsenceBehindItTheStoredStageStands()
    {
        Assert.Equal(OhStage.AbsenceRecorded, OhStages.Of(At(OhStage.AbsenceRecorded), Today));
    }

    // ── which moves are legitimate ─────────────────────────────────────────

    [Fact]
    public void ACaseMovesForward()
    {
        Assert.True(OhStages.CanMove(OhStage.HrAlerted, OhStage.Referred));
    }

    // A clinician who sends recommendations with the assessment report moves
    // two stages at once, and forcing the intermediate click would record a
    // step that did not happen separately.
    [Fact]
    public void ACaseCanSkipForward()
    {
        Assert.True(OhStages.CanMove(OhStage.Referred, OhStage.RecommendationsReceived));
    }

    // A case that has moved on has moved on. Correcting a mistake is a note,
    // not a rewind.
    [Fact]
    public void ACaseDoesNotGoBackwards()
    {
        Assert.False(OhStages.CanMove(OhStage.RecommendationsReceived, OhStage.Referred));
        Assert.False(OhStages.CanMove(OhStage.Returned, OhStage.HrReview));
    }

    // People recover, leave, or withdraw consent. A case that cannot be closed
    // from where it stands is one that stays on the board for good.
    [Theory]
    [InlineData(OhStage.Monitoring)]
    [InlineData(OhStage.Referred)]
    [InlineData(OhStage.HrReview)]
    [InlineData(OhStage.RtwCompleted)]
    public void ACaseCanBeClosedFromAnywhere(OhStage from)
    {
        Assert.True(OhStages.CanMove(from, OhStage.Closed));
    }

    // What reopens a closed case is new information for HR to weigh. Sending it
    // back to Monitoring would discard everything the clinician said.
    [Fact]
    public void ReopeningLandsAtHrReview()
    {
        Assert.True(OhStages.CanMove(OhStage.Closed, OhStage.HrReview));
        Assert.False(OhStages.CanMove(OhStage.Closed, OhStage.Monitoring));
        Assert.False(OhStages.CanMove(OhStage.Closed, OhStage.Referred));
    }

    [Fact]
    public void SavingWithoutChangingTheStageIsNotAMove()
    {
        Assert.True(OhStages.CanMove(OhStage.Referred, OhStage.Referred));
    }

    // Built from the rule rather than written out, so a picker cannot offer
    // something the service will refuse.
    [Fact]
    public void TheOfferedStagesAreOnesTheRuleAllows()
    {
        foreach (OhStage from in Enum.GetValues<OhStage>())
        {
            Assert.All(
                OhStages.NextFrom(from),
                to => Assert.True(OhStages.CanMove(from, to)));
        }
    }

    // ── what a stage needs before it can be claimed ────────────────────────

    // The screen has a free stage dropdown, so a case can read "Assessment
    // completed" with no assessment date — which makes the board's own columns
    // unreliable.
    [Fact]
    public void ReferringNeedsAReferralDate()
    {
        Assert.Equal(
            OhRefusal.ReferralDateRequired,
            OhRules.CheckMove(At(OhStage.HrAlerted), OhStage.Referred, adjustmentCount: 0));
    }

    [Fact]
    public void CompletingAnAssessmentNeedsItsDate()
    {
        Assert.Equal(
            OhRefusal.AssessmentDateRequired,
            OhRules.CheckMove(
                At(OhStage.Referred, referred: Today.AddDays(-5)),
                OhStage.AssessmentCompleted,
                adjustmentCount: 0));
    }

    // A clinician's advice is the fitness opinion. Reaching this stage with it
    // still pending means the advice has not actually arrived.
    [Fact]
    public void RecommendationsNeedAFitnessOpinion()
    {
        Assert.Equal(
            OhRefusal.FitnessRequired,
            OhRules.CheckMove(
                At(OhStage.AssessmentCompleted, assessed: Today.AddDays(-2)),
                OhStage.RecommendationsReceived,
                adjustmentCount: 0));
    }

    // A case claiming implemented adjustments with none recorded is the board
    // reporting work that does not exist.
    [Fact]
    public void ImplementedAdjustmentsNeedAdjustments()
    {
        Assert.Equal(
            OhRefusal.DescriptionRequired,
            OhRules.CheckMove(
                At(OhStage.HrReview, fitness: OhFitness.FitWithAdjustments),
                OhStage.AdjustmentsImplemented,
                adjustmentCount: 0));
    }

    [Fact]
    public void AWellFoundedMoveIsAccepted()
    {
        Assert.Equal(
            OhRefusal.None,
            OhRules.CheckMove(
                At(OhStage.HrReview, fitness: OhFitness.FitWithAdjustments),
                OhStage.AdjustmentsImplemented,
                adjustmentCount: 2));
    }

    [Fact]
    public void ClosingNeedsNothing()
    {
        Assert.Equal(
            OhRefusal.None,
            OhRules.CheckMove(At(OhStage.Monitoring), OhStage.Closed, adjustmentCount: 0));
    }

    // ── dates that cannot be ───────────────────────────────────────────────

    [Fact]
    public void AnAssessmentCannotPredateTheReferral()
    {
        Assert.Equal(
            OhRefusal.AssessmentBeforeReferral,
            OhRules.CheckDates(At(
                OhStage.AssessmentCompleted,
                referred: new DateOnly(2026, 9, 10),
                assessed: new DateOnly(2026, 9, 1))));
    }

    [Fact]
    public void AReturnCannotPredateTheAbsence()
    {
        OhReferral referral = At(OhStage.Referred, absenceStart: new DateOnly(2026, 8, 1));
        referral.ExpectedReturnDate = new DateOnly(2026, 7, 1);

        Assert.Equal(OhRefusal.ReturnBeforeAbsenceStart, OhRules.CheckDates(referral));
    }

    [Fact]
    public void NotesHaveACeiling()
    {
        OhReferral referral = At(OhStage.Referred);
        referral.CaseNotes = new string('x', OhRules.MaximumNotesLength + 1);

        Assert.Equal(OhRefusal.NotesTooLong, OhRules.CheckDates(referral));
    }

    [Fact]
    public void GoodDatesAreAccepted()
    {
        Assert.Equal(
            OhRefusal.None,
            OhRules.CheckDates(At(
                OhStage.AssessmentCompleted,
                absenceStart: new DateOnly(2026, 8, 1),
                referred: new DateOnly(2026, 9, 1),
                assessed: new DateOnly(2026, 9, 10))));
    }

    // ── how long the absence has run ───────────────────────────────────────

    [Fact]
    public void TheAbsenceIsCountedToToday()
    {
        Assert.Equal(
            40, OhRules.AbsenceDays(At(OhStage.Referred, absenceStart: Today.AddDays(-40)), Today));
    }

    // Without this a closed case's absence grows for ever, and the longest
    // absences on the board are all people who came back years ago.
    [Fact]
    public void AClosedCasesAbsenceStopsCountingOnTheDayItClosed()
    {
        Assert.Equal(
            20,
            OhRules.AbsenceDays(
                At(OhStage.Closed, absenceStart: Today.AddDays(-50), closed: Today.AddDays(-30)),
                Today));
    }

    [Fact]
    public void WithNoAbsenceThereIsNoCount()
    {
        Assert.Null(OhRules.AbsenceDays(At(OhStage.Referred), Today));
    }

    // ── reviews ────────────────────────────────────────────────────────────

    [Fact]
    public void AReviewDateReachedIsDue()
    {
        OhReferral referral = At(OhStage.RtwCompleted);
        referral.ReviewDate = Today;

        Assert.True(OhRules.ReviewIsDue(referral, Today));
    }

    [Fact]
    public void AReviewStillAheadIsNotDue()
    {
        OhReferral referral = At(OhStage.RtwCompleted);
        referral.ReviewDate = Today.AddDays(7);

        Assert.False(OhRules.ReviewIsDue(referral, Today));
    }

    // A closed case needs no review. Counting them would fill the figure with
    // work nobody can complete.
    [Fact]
    public void AClosedCaseHasNoReviewDue()
    {
        OhReferral referral = At(OhStage.Closed);
        referral.ReviewDate = Today.AddDays(-30);

        Assert.False(OhRules.ReviewIsDue(referral, Today));
    }

    // ── what happens next ──────────────────────────────────────────────────

    // Derived from the stage, so it cannot keep saying "chase the appointment"
    // after the appointment happened.
    [Fact]
    public void TheNextActionFollowsTheStage()
    {
        OhNextAction? referred = OhNextActions.Of(
            At(OhStage.Referred, referred: Today.AddDays(-3)), Today);

        Assert.NotNull(referred);
        Assert.Contains("appointment", referred.Label);

        OhNextAction? assessed = OhNextActions.Of(
            At(OhStage.AssessmentCompleted, assessed: Today.AddDays(-1)), Today);

        Assert.NotNull(assessed);
        Assert.Contains("report", assessed.Label);
    }

    // At some stages the move is the clinician's, and they are not in this
    // system at all. Naming the case owner would put work on an HR list that HR
    // cannot do.
    [Fact]
    public void WaitingOnTheClinicianSaysSo()
    {
        OhNextAction? next = OhNextActions.Of(
            At(OhStage.Referred, referred: Today.AddDays(-3)), Today);

        Assert.Equal("Occupational health provider", next!.Owner);
    }

    [Fact]
    public void AnAbsencePastTheThresholdAsksForAnAlert()
    {
        OhNextAction? next = OhNextActions.Of(
            At(OhStage.Monitoring, absenceStart: Today.AddDays(-40)), Today);

        Assert.NotNull(next);
        Assert.Contains("alert HR", next.Label);
    }

    // An action on a closed case is how a board fills up with work nobody can
    // complete.
    [Fact]
    public void AClosedCaseHasNothingNext()
    {
        Assert.Null(OhNextActions.Of(At(OhStage.Closed), Today));
    }

    [Fact]
    public void EveryOpenStageHasSomethingNext()
    {
        foreach (OhStage stage in Enum.GetValues<OhStage>().Where(s => s != OhStage.Closed))
        {
            OhReferral referral = At(stage, absenceStart: Today.AddDays(-40));

            Assert.NotNull(OhNextActions.Of(referral, Today));
        }
    }
}
