using Motee.Domain.Recruitment;

namespace Motee.Tests.Unit;

// The gates between pipeline stages.
//
// Every one of these exists because of a way the evidence goes missing, and
// each is cheap to get subtly wrong: the difference between "has an offer" and
// "has an accepted offer" is one word and somebody being onboarded before they
// said yes. Pure rules, so they are tested without a database.
//
// The frontend enforces the same rules in its board. That is the reason to hold
// them here as well rather than instead — a rule living only in one client is a
// rule every other client walks past.
public class RecruitmentPipelineTests
{
    private static readonly IReadOnlyList<CandidateStage> EveryStage =
        RecruitmentPipeline.Order;

    private static Candidate At(
        CandidateStage stage, CandidateStatus status = CandidateStatus.Active) => new()
        {
            Id = Guid.NewGuid(),
            FirstName = "Chidi",
            LastName = "Okafor",
            Email = "chidi@example.com",
            Stage = stage,
            Status = status,
        };

    private static Scorecard Scored(int score = 4) => new()
    {
        Id = Guid.NewGuid(),
        Criteria = [new ScorecardCriterion { Label = "Technical", Score = score }],
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static CandidateOffer Offer(
        OfferStatus status = OfferStatus.Sent, int daysAgo = 0) => new()
        {
            Id = Guid.NewGuid(),
            Status = status,
            SentAt = DateTimeOffset.UtcNow.AddDays(-daysAgo),
        };

    private static MoveRefusal Move(
        Candidate candidate,
        CandidateStage to,
        IReadOnlyList<CandidateStage>? stages = null,
        IReadOnlyCollection<Scorecard>? scorecards = null,
        IReadOnlyCollection<CandidateOffer>? offers = null,
        int openings = 1,
        int hired = 0) =>
        RecruitmentPipeline.Refusal(
            candidate, to, stages ?? EveryStage, scorecards ?? [], offers ?? [], openings, hired);

    [Fact]
    public void AnApplicantMovesToTheInterviewStage() =>
        Assert.Equal(
            MoveRefusal.None, Move(At(CandidateStage.Applicants), CandidateStage.Interview));

    [Fact]
    public void MovingSomebodyWhereTheyAlreadyAreIsRefused() =>
        Assert.Equal(
            MoveRefusal.AlreadyThere,
            Move(At(CandidateStage.Interview), CandidateStage.Interview));

    // Restoring them is a separate, deliberate act. Otherwise dragging a card
    // quietly resurrects somebody who was turned down.
    [Fact]
    public void ARejectedCandidateDoesNotMove() =>
        Assert.Equal(
            MoveRefusal.Rejected,
            Move(
                At(CandidateStage.Interview, CandidateStatus.Rejected),
                CandidateStage.Interviewed,
                scorecards: [Scored()]));

    [Fact]
    public void AStageTheRoleDoesNotUseIsRefused() =>
        Assert.Equal(
            MoveRefusal.StageNotEnabled,
            Move(
                At(CandidateStage.Applicants),
                CandidateStage.Interview,
                stages: [CandidateStage.Applicants, CandidateStage.Offer, CandidateStage.Hired]));

    // Skipping loses the interview or offer record, which is the evidence the
    // later stages are read through.
    [Fact]
    public void JumpingTwoStagesIsRefused() =>
        Assert.Equal(
            MoveRefusal.SkipsAStage,
            Move(At(CandidateStage.Applicants), CandidateStage.Interviewed));

    // Named before the gates, so somebody dragged from Applicant to Interviewed
    // is told to go via the interview rather than to score a meeting that was
    // never arranged.
    [Fact]
    public void SkippingIsReportedBeforeTheMissingScore() =>
        Assert.Equal(
            MoveRefusal.SkipsAStage,
            Move(At(CandidateStage.Applicants), CandidateStage.Interviewed, scorecards: []));

    // A correction, not an error. Refusing it would leave rejection as the only
    // way to undo a misdrop.
    [Fact]
    public void MovingSomebodyBackAStageIsAllowed() =>
        Assert.Equal(
            MoveRefusal.None,
            Move(At(CandidateStage.Interviewed), CandidateStage.Interview));

    // ── the score gate ──────────────────────────────────────────────────────

    [Fact]
    public void LeavingTheInterviewStageNeedsAScore() =>
        Assert.Equal(
            MoveRefusal.NotScored,
            Move(At(CandidateStage.Interview), CandidateStage.Interviewed));

    [Fact]
    public void AScoredCandidateLeavesTheInterviewStage() =>
        Assert.Equal(
            MoveRefusal.None,
            Move(At(CandidateStage.Interview), CandidateStage.Interviewed,
                scorecards: [Scored()]));

    // ── the offer gate ──────────────────────────────────────────────────────

    [Fact]
    public void NobodyIsHiredWithoutAnOffer() =>
        Assert.Equal(MoveRefusal.NoOffer, Move(At(CandidateStage.Offer), CandidateStage.Hired));

    [Fact]
    public void AnUnansweredOfferIsNotAnAcceptance() =>
        Assert.Equal(
            MoveRefusal.OfferNotAccepted,
            Move(At(CandidateStage.Offer), CandidateStage.Hired, offers: [Offer()]));

    // A different refusal from "not yet", because it means something different
    // to whoever is looking at the board.
    [Fact]
    public void ADeclinedOfferEndsThePipelineRatherThanPausingIt() =>
        Assert.Equal(
            MoveRefusal.OfferDeclined,
            Move(
                At(CandidateStage.Offer),
                CandidateStage.Hired,
                offers: [Offer(OfferStatus.Rejected)]));

    [Fact]
    public void AnAcceptedOfferOpensTheWayToHired() =>
        Assert.Equal(
            MoveRefusal.None,
            Move(
                At(CandidateStage.Offer),
                CandidateStage.Hired,
                offers: [Offer(OfferStatus.Accepted)]));

    // Somebody who declined and was then persuaded has accepted. Reading only
    // the latest offer would be right; reading only the first would not.
    [Fact]
    public void AnImprovedOfferAfterARefusalStillCounts() =>
        Assert.Equal(
            MoveRefusal.None,
            Move(
                At(CandidateStage.Offer),
                CandidateStage.Hired,
                offers: [Offer(OfferStatus.Rejected, daysAgo: 7), Offer(OfferStatus.Accepted)]));

    // Held even when the offer stage is switched off for the role. A company
    // that makes no formal offers still must not hire somebody who declined.
    [Fact]
    public void TheOfferGateHoldsWhenTheOfferStageIsSwitchedOff() =>
        Assert.Equal(
            MoveRefusal.NoOffer,
            Move(
                At(CandidateStage.Interviewed),
                CandidateStage.Hired,
                stages:
                [
                    CandidateStage.Applicants,
                    CandidateStage.Interview,
                    CandidateStage.Interviewed,
                    CandidateStage.Hired,
                ]));

    // ── capacity ────────────────────────────────────────────────────────────

    // Finance signed off a number of seats, not an intention.
    [Fact]
    public void ARoleCannotSeatMoreHiresThanItWasFundedFor() =>
        Assert.Equal(
            MoveRefusal.NoOpeningsLeft,
            Move(
                At(CandidateStage.Offer),
                CandidateStage.Hired,
                offers: [Offer(OfferStatus.Accepted)],
                openings: 2,
                hired: 2));

    [Fact]
    public void TheSecondOfTwoOpeningsIsStillOpen() =>
        Assert.Equal(
            MoveRefusal.None,
            Move(
                At(CandidateStage.Offer),
                CandidateStage.Hired,
                offers: [Offer(OfferStatus.Accepted)],
                openings: 2,
                hired: 1));

    // ── the stage list ──────────────────────────────────────────────────────

    // A list written before a stage existed would otherwise drop that stage
    // from the role forever, and a role with no entry or no exit is not a
    // pipeline at all.
    [Fact]
    public void TheEntryAndExitStagesArePutBackIfLeftOut()
    {
        IReadOnlyList<CandidateStage> stages =
            RecruitmentPipeline.StagesFor([CandidateStage.Interview]);

        Assert.Equal(
            [CandidateStage.Applicants, CandidateStage.Interview, CandidateStage.Hired],
            stages);
    }

    [Fact]
    public void StagesComeBackInPipelineOrderHoweverTheyWereGiven() =>
        Assert.Equal(
            [
                CandidateStage.Applicants,
                CandidateStage.Interview,
                CandidateStage.Interviewed,
                CandidateStage.Hired,
            ],
            RecruitmentPipeline.StagesFor(
                [CandidateStage.Interviewed, CandidateStage.Hired, CandidateStage.Interview]));
}

// What the vacancy's badge says, and where that comes from.
public class RequisitionStatusTests
{
    private static Candidate At(
        CandidateStage stage, CandidateStatus status = CandidateStatus.Active) => new()
        {
            Id = Guid.NewGuid(),
            FirstName = "Chidi",
            LastName = "Okafor",
            Email = "chidi@example.com",
            Stage = stage,
            Status = status,
        };

    // A draft is a draft however many people have applied to it. Deriving it
    // from the pipeline would let an unpublished vacancy report itself as
    // interviewing.
    [Theory]
    [InlineData(RequisitionStatus.Draft)]
    [InlineData(RequisitionStatus.PendingApproval)]
    [InlineData(RequisitionStatus.OnHold)]
    [InlineData(RequisitionStatus.Closed)]
    [InlineData(RequisitionStatus.Cancelled)]
    public void PaperworkStatusesAreLeftAlone(RequisitionStatus status) =>
        Assert.Equal(
            status,
            Requisitions.Derive(status, 1, [At(CandidateStage.Interview)]));

    [Fact]
    public void AVacancyWithOnlyApplicantsReadsAsOpen() =>
        Assert.Equal(
            RequisitionStatus.Open,
            Requisitions.Derive(
                RequisitionStatus.Interviewing, 1, [At(CandidateStage.Applicants)]));

    [Fact]
    public void SomebodyInTheInterviewStagesMakesItInterviewing() =>
        Assert.Equal(
            RequisitionStatus.Interviewing,
            Requisitions.Derive(RequisitionStatus.Open, 1, [At(CandidateStage.Interviewed)]));

    [Fact]
    public void SomebodyAtOfferOutranksSomebodyInterviewing() =>
        Assert.Equal(
            RequisitionStatus.OfferStage,
            Requisitions.Derive(
                RequisitionStatus.Open,
                2,
                [At(CandidateStage.Interview), At(CandidateStage.Offer)]));

    [Fact]
    public void EveryOpeningFilledMakesItFilled() =>
        Assert.Equal(
            RequisitionStatus.Filled,
            Requisitions.Derive(
                RequisitionStatus.Open,
                2,
                [At(CandidateStage.Hired), At(CandidateStage.Hired), At(CandidateStage.Offer)]));

    [Fact]
    public void OneHireAgainstTwoOpeningsIsNotFilled() =>
        Assert.Equal(
            RequisitionStatus.OfferStage,
            Requisitions.Derive(
                RequisitionStatus.Open,
                2,
                [At(CandidateStage.Hired), At(CandidateStage.Offer)]));

    // Rejecting somebody after they started is an offboarding question, not a
    // vacancy one — the seat is still taken.
    [Fact]
    public void AHireRejectedAfterwardsStillOccupiesTheirSeat() =>
        Assert.Equal(
            RequisitionStatus.Filled,
            Requisitions.Derive(
                RequisitionStatus.Open,
                1,
                [At(CandidateStage.Hired, CandidateStatus.Rejected)]));

    // Rejected candidates do not keep a vacancy looking busy.
    [Fact]
    public void RejectedCandidatesDoNotDriveTheStatus() =>
        Assert.Equal(
            RequisitionStatus.Open,
            Requisitions.Derive(
                RequisitionStatus.Interviewing,
                1,
                [At(CandidateStage.Offer, CandidateStatus.Rejected)]));

    [Fact]
    public void AnInterviewingVacancyIsStillRecruiting() =>
        Assert.True(Requisitions.IsRecruiting(RequisitionStatus.Interviewing));

    [Fact]
    public void AFilledVacancyIsNot() =>
        Assert.False(Requisitions.IsRecruiting(RequisitionStatus.Filled));

    // Derived from the date, like an announcement's expiry, so it is right
    // without a job having run.
    [Fact]
    public void AVacancyPastItsClosingDateTakesNoMoreApplications()
    {
        JobRequisition requisition = new()
        {
            PositionTitle = "Engineer",
            Status = RequisitionStatus.Open,
            ExpiryDate = new DateOnly(2026, 6, 1),
        };

        Assert.True(Requisitions.HasExpired(requisition, new DateOnly(2026, 6, 2)));
        Assert.False(
            Requisitions.IsAcceptingApplications(requisition, new DateOnly(2026, 6, 2)));

        // Still open on the closing day itself.
        Assert.False(Requisitions.HasExpired(requisition, new DateOnly(2026, 6, 1)));
        Assert.True(Requisitions.IsAcceptingApplications(requisition, new DateOnly(2026, 6, 1)));
    }
}

// The headline score, and why it is not a column.
public class ScorecardTests
{
    private static Scorecard With(params int[] scores) => new()
    {
        Id = Guid.NewGuid(),
        Criteria =
        [
            .. scores.Select((score, index) => new ScorecardCriterion
            {
                Label = $"Criterion {index + 1}",
                Score = score,
            }),
        ],
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void TheOverallIsTheAverageOfTheLines() =>
        Assert.Equal(3.7m, Scorecards.Overall(With(3, 4, 4).Criteria));

    // Not zero. Nobody having met them is a different fact from everybody
    // having rated them nought.
    [Fact]
    public void AScorecardWithNoLinesHasNoOverall() =>
        Assert.Null(Scorecards.Overall(With().Criteria));

    [Fact]
    public void ACandidatesScoreIsTheAverageAcrossThePanel() =>
        Assert.Equal(3.5m, Scorecards.AverageFor([With(4, 4), With(3, 3)]));

    [Fact]
    public void AnUnratedCandidateHasNoScore() =>
        Assert.Null(Scorecards.AverageFor([]));
}
