using Motee.Domain.Company;
using Motee.Domain.Common;
using Motee.Domain.Countries;

namespace Motee.Tests.Company;

// A company's registration and tax records, and whether anybody has checked
// them.
//
// The decision these are really about is who may move one. The screen gives the
// company a status dropdown containing Verified, so a tenant can mark their own
// registration verified without anybody looking at anything — and a company's
// own claim about its own incorporation is precisely what a verification is
// supposed to test. The company drafts and submits; Motee staff review.
//
// Written before the rules.
public class CompanyVerificationTests
{
    private static CompanyVerification At(
        VerificationStage stage,
        string? number = "RC-123456",
        Guid? document = null) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Kind = VerificationKind.Registration,
            Number = number,
            DocumentFileId = document ?? Guid.NewGuid(),
            Stage = stage,
        };

    // ── who may move it ────────────────────────────────────────────────────

    [Fact]
    public void ACompanyCanHandItOver()
    {
        Assert.True(VerificationFlow.CompanyCanMove(
            VerificationStage.Draft, VerificationStage.Submitted));
    }

    // The defect this closes: a tenant marking their own registration verified.
    [Fact]
    public void ACompanyCannotVerifyItself()
    {
        Assert.False(VerificationFlow.CompanyCanMove(
            VerificationStage.Draft, VerificationStage.Verified));
        Assert.False(VerificationFlow.CompanyCanMove(
            VerificationStage.Submitted, VerificationStage.Verified));
    }

    [Fact]
    public void ACompanyCannotRejectItsOwnPaperworkEither()
    {
        Assert.False(VerificationFlow.CompanyCanMove(
            VerificationStage.Submitted, VerificationStage.Rejected));
    }

    // A company that spots a wrong number should not have to wait to be
    // rejected.
    [Fact]
    public void ACompanyCanWithdrawSomethingNobodyHasLookedAtYet()
    {
        Assert.True(VerificationFlow.CompanyCanMove(
            VerificationStage.Submitted, VerificationStage.Draft));
    }

    // Pulling it back mid-check would waste the reviewer's work and let the
    // document change after it had been seen.
    [Fact]
    public void ACompanyCannotPullBackSomethingUnderReview()
    {
        Assert.False(VerificationFlow.CompanyCanMove(
            VerificationStage.UnderReview, VerificationStage.Draft));
    }

    [Fact]
    public void ACompanyCannotUndoAVerification()
    {
        Assert.All(
            Enum.GetValues<VerificationStage>(),
            to => Assert.False(
                VerificationFlow.CompanyCanMove(VerificationStage.Verified, to),
                $"A company should not be able to move a verified record to {to}."));
    }

    // Fix and resubmit, through Draft so the number and document can be
    // corrected first.
    [Fact]
    public void ARejectionCanBeFixedAndSentAgain()
    {
        Assert.True(VerificationFlow.CompanyCanMove(
            VerificationStage.Rejected, VerificationStage.Draft));
    }

    // ── what the reviewer may do ───────────────────────────────────────────

    [Fact]
    public void ThePlatformCanVerifyOrReject()
    {
        Assert.True(VerificationFlow.PlatformCanMove(
            VerificationStage.Submitted, VerificationStage.Verified));
        Assert.True(VerificationFlow.PlatformCanMove(
            VerificationStage.Submitted, VerificationStage.Rejected));
    }

    // Reviewing a draft would be checking something the company has not
    // finished.
    [Fact]
    public void ThereIsNothingToReviewUntilItIsHandedOver()
    {
        Assert.All(
            Enum.GetValues<VerificationStage>(),
            to => Assert.False(
                VerificationFlow.PlatformCanMove(VerificationStage.Draft, to),
                $"A draft should not be reviewable into {to}."));
    }

    // A company dissolved, or a certificate found to be forged. Possible
    // without deleting the record of it having been granted.
    [Fact]
    public void AVerificationCanBeWithdrawn()
    {
        Assert.True(VerificationFlow.PlatformCanMove(
            VerificationStage.Verified, VerificationStage.Rejected));
    }

    [Fact]
    public void ARejectionCanBeReconsidered()
    {
        Assert.True(VerificationFlow.PlatformCanMove(
            VerificationStage.Rejected, VerificationStage.Verified));
    }

    // ── what is editable ───────────────────────────────────────────────────

    // Once submitted, the reviewer is looking at a particular certificate.
    // Letting the number change underneath them means the thing verified is not
    // the thing on file.
    [Theory]
    [InlineData(VerificationStage.Submitted)]
    [InlineData(VerificationStage.UnderReview)]
    [InlineData(VerificationStage.Verified)]
    [InlineData(VerificationStage.Rejected)]
    public void TheNumberCannotChangeOnceItIsOutOfDraft(VerificationStage stage)
    {
        Assert.Equal(
            VerificationRefusal.NotEditableNow,
            VerificationRules.CheckDetails(At(stage), "RC-999"));
    }

    [Fact]
    public void TheNumberCanChangeWhileItIsADraft()
    {
        Assert.Equal(
            VerificationRefusal.None,
            VerificationRules.CheckDetails(At(VerificationStage.Draft), "RC-999"));
    }

    [Fact]
    public void ANumberHasACeiling()
    {
        Assert.Equal(
            VerificationRefusal.NumberRequired,
            VerificationRules.CheckDetails(
                At(VerificationStage.Draft),
                new string('9', VerificationRules.MaximumNumberLength + 1)));
    }

    // ── submitting ─────────────────────────────────────────────────────────

    // A submission with nothing attached is a reviewer being asked to verify a
    // blank form, and the screen allows exactly that.
    [Fact]
    public void SubmittingWithoutANumberIsRefused()
    {
        Assert.Equal(
            VerificationRefusal.NumberRequired,
            VerificationRules.CheckSubmission(At(VerificationStage.Draft, number: "  ")));
    }

    [Fact]
    public void SubmittingWithoutTheCertificateIsRefused()
    {
        CompanyVerification verification = At(VerificationStage.Draft);
        verification.DocumentFileId = null;

        Assert.Equal(
            VerificationRefusal.DocumentRequired,
            VerificationRules.CheckSubmission(verification));
    }

    [Fact]
    public void ACompleteSubmissionIsAccepted()
    {
        Assert.Equal(
            VerificationRefusal.None,
            VerificationRules.CheckSubmission(At(VerificationStage.Draft)));
    }

    [Fact]
    public void SubmittingSomethingAlreadyUnderReviewIsRefused()
    {
        Assert.Equal(
            VerificationRefusal.StageNotReachable,
            VerificationRules.CheckSubmission(At(VerificationStage.UnderReview)));
    }

    // ── reviewing ──────────────────────────────────────────────────────────

    // "Rejected" with no reason is a dead end for whoever has to fix it.
    [Fact]
    public void ARejectionNeedsAReason()
    {
        Assert.Equal(
            VerificationRefusal.ReasonRequired,
            VerificationRules.CheckReview(
                At(VerificationStage.Submitted), VerificationStage.Rejected, note: "  "));
    }

    [Fact]
    public void ARejectionWithAReasonIsAccepted()
    {
        Assert.Equal(
            VerificationRefusal.None,
            VerificationRules.CheckReview(
                At(VerificationStage.Submitted),
                VerificationStage.Rejected,
                note: "The certificate is for a different company."));
    }

    [Fact]
    public void AVerificationNeedsNoReason()
    {
        Assert.Equal(
            VerificationRefusal.None,
            VerificationRules.CheckReview(
                At(VerificationStage.Submitted), VerificationStage.Verified, note: null));
    }

    [Fact]
    public void ReviewingADraftIsRefused()
    {
        Assert.Equal(
            VerificationRefusal.StageNotReachable,
            VerificationRules.CheckReview(
                At(VerificationStage.Draft), VerificationStage.Verified, note: null));
    }

    // ── where it stands ────────────────────────────────────────────────────

    [Fact]
    public void SubmittedAndUnderReviewAreWaitingOnUs()
    {
        Assert.True(VerificationFlow.AwaitsReview(VerificationStage.Submitted));
        Assert.True(VerificationFlow.AwaitsReview(VerificationStage.UnderReview));
        Assert.False(VerificationFlow.AwaitsReview(VerificationStage.Draft));
        Assert.False(VerificationFlow.AwaitsReview(VerificationStage.Verified));
    }

    [Fact]
    public void VerifiedAndRejectedAreSettled()
    {
        Assert.True(VerificationFlow.IsSettled(VerificationStage.Verified));
        Assert.True(VerificationFlow.IsSettled(VerificationStage.Rejected));
        Assert.False(VerificationFlow.IsSettled(VerificationStage.Submitted));
    }

    // ── the labels are the country's ───────────────────────────────────────

    // A screen asking a Nigerian company for its "Company Number" is asking for
    // something that does not exist.
    [Fact]
    public void NigeriaRegistersWithTheCacAndHoldsATin()
    {
        ICountryProfile nigeria = new NigeriaProfile();

        Assert.Equal("RC Number", nigeria.Registration.NumberLabel);
        Assert.Equal("Corporate Affairs Commission", nigeria.Registration.Authority);
        Assert.Equal("TIN", nigeria.TaxIdentifier.NumberLabel);
    }

    [Fact]
    public void TheUkRegistersWithCompaniesHouseAndHoldsAVatNumber()
    {
        ICountryProfile uk = new UnitedKingdomProfile();

        Assert.Equal("Company Number", uk.Registration.NumberLabel);
        Assert.Equal("Companies House", uk.Registration.Authority);
        Assert.Equal("VAT Number", uk.TaxIdentifier.NumberLabel);
        Assert.Equal("HM Revenue & Customs", uk.TaxIdentifier.Authority);
    }

    [Fact]
    public void EveryCountryNamesBothIdentifiersAndWhoIssuesThem()
    {
        foreach (ICountryProfile profile in (ICountryProfile[])
            [new NigeriaProfile(), new UnitedKingdomProfile()])
        {
            foreach (CompanyIdentifier identifier in (CompanyIdentifier[])
                [profile.Registration, profile.TaxIdentifier])
            {
                Assert.False(string.IsNullOrWhiteSpace(identifier.Label));
                Assert.False(string.IsNullOrWhiteSpace(identifier.NumberLabel));
                Assert.False(string.IsNullOrWhiteSpace(identifier.Authority));
                Assert.False(string.IsNullOrWhiteSpace(identifier.Description));
            }
        }
    }

    [Fact]
    public void BothCountriesWeServeHaveAProfile()
    {
        Assert.Equal(CountryCode.Nigeria, new NigeriaProfile().CountryCode);
        Assert.Equal(CountryCode.UnitedKingdom, new UnitedKingdomProfile().CountryCode);
    }
}
