using Motee.Domain.Workforce;

namespace Motee.Tests.Unit;

// Where one hiring effort has got to, across the six stages.
//
// The precedence is the whole of it, and it is the thing that is easy to get
// backwards. A vacancy can be true of several stages at once — two people in
// interviews and a third with an offer out — so the resolver takes the
// furthest along. Reading the earliest true stage instead would report every
// nearly-finished hire as though it had just started, which is exactly the
// report somebody uses to decide where to intervene.
public class HiringChainTests
{
    private static ChainFacts Chain(
        bool hasVacancy = true,
        bool published = true,
        bool approved = true,
        int applicants = 0,
        int inSelection = 0,
        int atOffer = 0,
        int hiredNotInvited = 0,
        bool preBoarding = false,
        bool onboarding = false) => new()
        {
            HasVacancy = hasVacancy,
            VacancyPublished = published,
            VacancyApproved = approved,
            Applicants = applicants,
            InSelection = inSelection,
            AtOffer = atOffer,
            HiredNotInvited = hiredNotInvited,
            PreBoarding = preBoarding,
            Onboarding = onboarding,
        };

    // ── one stage at a time ─────────────────────────────────────────────────

    [Fact]
    public void ARequestWithNoVacancyIsStillPlanning() =>
        Assert.Equal(
            HiringStage.WorkforcePlanning,
            HiringChain.Resolve(Chain(hasVacancy: false, published: false)));

    // Approved but not advertised is the requisition stage, not attract.
    // Nobody can apply to a vacancy that was never published.
    [Fact]
    public void AVacancyNotYetAdvertisedIsAtRequisition() =>
        Assert.Equal(
            HiringStage.Requisition, HiringChain.Resolve(Chain(published: false)));

    [Fact]
    public void AnAdvertisedVacancyIsAttracting() =>
        Assert.Equal(HiringStage.Attract, HiringChain.Resolve(Chain()));

    [Fact]
    public void CandidatesInInterviewsAreSelection() =>
        Assert.Equal(HiringStage.Select, HiringChain.Resolve(Chain(inSelection: 3)));

    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(0, 0, true)]
    public void AnOfferAHireOrChecksAreAllPreEmployment(
        int atOffer, int hiredNotInvited, bool preBoarding) =>
        Assert.Equal(
            HiringStage.PreEmployment,
            HiringChain.Resolve(Chain(
                atOffer: atOffer,
                hiredNotInvited: hiredNotInvited,
                preBoarding: preBoarding)));

    [Fact]
    public void OnboardingUnderWayIsTheLastStage() =>
        Assert.Equal(HiringStage.Onboard, HiringChain.Resolve(Chain(onboarding: true)));

    // ── precedence ──────────────────────────────────────────────────────────

    // The case the precedence exists for. Two people still interviewing and
    // one with an accepted offer is a role that is nearly filled.
    [Fact]
    public void TheFurthestAlongCandidateDecidesTheStage() =>
        Assert.Equal(
            HiringStage.PreEmployment,
            HiringChain.Resolve(Chain(applicants: 9, inSelection: 2, atOffer: 1)));

    [Fact]
    public void OnboardingOutranksEverythingElse() =>
        Assert.Equal(
            HiringStage.Onboard,
            HiringChain.Resolve(Chain(
                applicants: 4, inSelection: 2, atOffer: 1, preBoarding: true, onboarding: true)));

    [Fact]
    public void ApplicantsAloneDoNotReachSelection() =>
        Assert.Equal(HiringStage.Attract, HiringChain.Resolve(Chain(applicants: 12)));

    // Somebody hired against a vacancy nobody published is still at
    // pre-employment: the hire happened, however irregularly.
    [Fact]
    public void AHireOnAnUnpublishedVacancyIsStillPreEmployment() =>
        Assert.Equal(
            HiringStage.PreEmployment,
            HiringChain.Resolve(Chain(published: false, hiredNotInvited: 1)));

    // ── the step number ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(HiringStage.WorkforcePlanning, 1)]
    [InlineData(HiringStage.Requisition, 2)]
    [InlineData(HiringStage.Attract, 3)]
    [InlineData(HiringStage.Select, 4)]
    [InlineData(HiringStage.PreEmployment, 5)]
    [InlineData(HiringStage.Onboard, 6)]
    public void EachStageKnowsItsPlaceInTheSix(HiringStage stage, int step) =>
        Assert.Equal(step, HiringChain.StepOf(stage));

    // ── stalling ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(40, true)]
    public void AnEffortThatHasNotMovedInTenDaysIsStalled(int days, bool expected) =>
        Assert.Equal(expected, HiringChain.IsStalled(days));

    // ── what is happening, in words ─────────────────────────────────────────

    // A hire who said yes and has heard nothing is the gap worth naming, so
    // the detail says so rather than "pre-employment".
    [Fact]
    public void AHireNobodyHasInvitedSaysSo() =>
        Assert.Equal(
            "Hired — onboarding invite not sent",
            HiringChain.Detail(HiringStage.PreEmployment, Chain(hiredNotInvited: 1)));

    [Fact]
    public void AnOfferOutSaysWhatItIsWaitingFor() =>
        Assert.Equal(
            "Offer out — awaiting acceptance",
            HiringChain.Detail(HiringStage.PreEmployment, Chain(atOffer: 1)));

    [Fact]
    public void ChecksBeforeStartingSaySo() =>
        Assert.Equal(
            "Checks in progress before they start",
            HiringChain.Detail(HiringStage.PreEmployment, Chain(preBoarding: true)));

    [Theory]
    [InlineData(1, "1 candidate in selection")]
    [InlineData(4, "4 candidates in selection")]
    public void SelectionCountsThePeopleInIt(int many, string expected) =>
        Assert.Equal(
            expected, HiringChain.Detail(HiringStage.Select, Chain(inSelection: many)));

    [Fact]
    public void AnAdvertWithNobodyOnItSaysThatToo() =>
        Assert.Equal(
            "Advertised — no applicants yet",
            HiringChain.Detail(HiringStage.Attract, Chain()));

    [Fact]
    public void AnAdvertWithApplicantsSaysHowManyToScreen() =>
        Assert.Equal(
            "7 applicants to screen",
            HiringChain.Detail(HiringStage.Attract, Chain(applicants: 7)));

    [Fact]
    public void AnUnapprovedVacancyIsWaitingOnApproval() =>
        Assert.Equal(
            "Vacancy awaiting approval",
            HiringChain.Detail(
                HiringStage.Requisition, Chain(published: false, approved: false)));

    [Fact]
    public void AnApprovedVacancyIsWaitingToBeAdvertised() =>
        Assert.Equal(
            "Approved — not advertised yet",
            HiringChain.Detail(HiringStage.Requisition, Chain(published: false)));

    [Fact]
    public void PlanningSaysWhatWasAsked() =>
        Assert.Equal(
            "Headcount requested",
            HiringChain.Detail(
                HiringStage.WorkforcePlanning, Chain(hasVacancy: false, published: false)));
}
