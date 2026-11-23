using Motee.Domain.Eor;

namespace Motee.Tests.Eor;

// Whether a worker employed abroad has the paperwork that makes it lawful.
//
// The most consequential thing on this screen and the least enforced. Each
// requirement is a dropdown with no expiry and no evidence, so a work permit
// that lapsed last month still reads "Complete" — and on an EOR register that
// is the right-to-work record. A new worker's employment contract defaults to
// complete, so somebody added a minute ago is recorded as having signed one.
//
// Written before the rules.
public class ComplianceTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    private static EorComplianceItem Item(
        ComplianceKey key,
        ComplianceState state = ComplianceState.Complete,
        DateOnly? expires = null) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            EngagementId = Guid.NewGuid(),
            Key = key,
            State = state,
            ExpiresOn = expires,
        };

    private static List<EorComplianceItem> AllComplete() =>
        [.. ComplianceCatalogue.All.Select(requirement => Item(requirement.Key))];

    // ── the catalogue ──────────────────────────────────────────────────────

    [Fact]
    public void EveryRequirementHasALabel()
    {
        Assert.All(
            ComplianceCatalogue.All,
            requirement => Assert.False(string.IsNullOrWhiteSpace(requirement.Label)));
    }

    [Fact]
    public void EveryKeyIsInTheCatalogueExactlyOnce()
    {
        foreach (ComplianceKey key in Enum.GetValues<ComplianceKey>())
        {
            Assert.Single(ComplianceCatalogue.All, requirement => requirement.Key == key);
        }
    }

    // A contract does not expire; a permit does. Sent to the client so the form
    // asks for an expiry where there is one to ask for.
    [Fact]
    public void OnlyTheThingsThatLapseCarryAnExpiry()
    {
        Assert.True(ComplianceCatalogue.Of(ComplianceKey.WorkPermit).Expires);
        Assert.True(ComplianceCatalogue.Of(ComplianceKey.LocalId).Expires);
        Assert.False(ComplianceCatalogue.Of(ComplianceKey.Contract).Expires);
    }

    // A right to work cannot be outstanding while somebody is working. A
    // statutory benefits enrolment catching up in the first weeks is normal,
    // and treating it as a bar would flag most new starters as unlawful.
    [Fact]
    public void RightToWorkBlocksWorkAndBenefitsEnrolmentDoesNot()
    {
        Assert.True(ComplianceCatalogue.Of(ComplianceKey.WorkPermit).BlocksWork);
        Assert.True(ComplianceCatalogue.Of(ComplianceKey.Contract).BlocksWork);
        Assert.True(ComplianceCatalogue.Of(ComplianceKey.TaxRegistration).BlocksWork);
        Assert.False(ComplianceCatalogue.Of(ComplianceKey.StatutoryBenefits).BlocksWork);
    }

    // ── where a requirement stands ─────────────────────────────────────────

    [Fact]
    public void SomethingCompleteWithNoExpiryStaysComplete()
    {
        Assert.Equal(
            ComplianceStanding.Complete,
            Compliance.StandingOf(Item(ComplianceKey.Contract), Today));
    }

    [Fact]
    public void SomethingOutstandingIsPending()
    {
        Assert.Equal(
            ComplianceStanding.Pending,
            Compliance.StandingOf(
                Item(ComplianceKey.WorkPermit, ComplianceState.Pending), Today));
    }

    [Fact]
    public void SomethingThatDoesNotApplyIsNotOutstanding()
    {
        ComplianceStanding standing = Compliance.StandingOf(
            Item(ComplianceKey.WorkPermit, ComplianceState.NotApplicable), Today);

        Assert.Equal(ComplianceStanding.NotApplicable, standing);
        Assert.True(Compliance.IsSatisfied(standing));
    }

    // The expiry wins over the tick, which is the whole reason the expiry
    // exists. Somebody ticking Complete in March does not make a permit valid
    // in December.
    [Fact]
    public void APermitPastItsExpiryIsExpiredWhateverTheDropdownSays()
    {
        Assert.Equal(
            ComplianceStanding.Expired,
            Compliance.StandingOf(
                Item(ComplianceKey.WorkPermit, expires: Today.AddDays(-1)), Today));
    }

    [Fact]
    public void APermitRunningOutSoonIsFlagged()
    {
        Assert.Equal(
            ComplianceStanding.ExpiringSoon,
            Compliance.StandingOf(
                Item(ComplianceKey.WorkPermit, expires: Today.AddDays(20)), Today));
    }

    [Fact]
    public void APermitWellInDateIsComplete()
    {
        Assert.Equal(
            ComplianceStanding.Complete,
            Compliance.StandingOf(
                Item(ComplianceKey.WorkPermit, expires: Today.AddDays(200)), Today));
    }

    [Fact]
    public void APermitExpiringTodayHasNotExpiredYet()
    {
        Assert.Equal(
            ComplianceStanding.ExpiringSoon,
            Compliance.StandingOf(Item(ComplianceKey.WorkPermit, expires: Today), Today));
    }

    // Expiring soon has not lapsed. Flagging the worker as non-compliant a
    // month early would make the register cry wolf.
    [Fact]
    public void ExpiringSoonStillCounts()
    {
        Assert.True(Compliance.IsSatisfied(ComplianceStanding.ExpiringSoon));
        Assert.False(Compliance.IsSatisfied(ComplianceStanding.Expired));
        Assert.False(Compliance.IsSatisfied(ComplianceStanding.Pending));
        Assert.False(Compliance.IsSatisfied(ComplianceStanding.ActionRequired));
    }

    // An expiry on something already marked outstanding must not promote it.
    [Fact]
    public void AnExpiryDoesNotMakeAnOutstandingThingComplete()
    {
        Assert.Equal(
            ComplianceStanding.Pending,
            Compliance.StandingOf(
                Item(ComplianceKey.WorkPermit, ComplianceState.Pending, Today.AddYears(1)),
                Today));
    }

    // ── what stands between a worker and being lawfully in post ────────────

    [Fact]
    public void AFullySignedOffWorkerHasNoBlockers()
    {
        Assert.Empty(Compliance.Blockers(AllComplete(), Today));
    }

    [Fact]
    public void AMissingRightToWorkBlocks()
    {
        List<EorComplianceItem> items = AllComplete();
        items.RemoveAll(item => item.Key == ComplianceKey.WorkPermit);
        items.Add(Item(ComplianceKey.WorkPermit, ComplianceState.Pending));

        Assert.Single(
            Compliance.Blockers(items, Today),
            blocker => blocker.Contains("Work permit"));
    }

    [Fact]
    public void AnExpiredPermitBlocksAndSaysWhen()
    {
        List<EorComplianceItem> items = AllComplete();
        items.RemoveAll(item => item.Key == ComplianceKey.WorkPermit);
        items.Add(Item(ComplianceKey.WorkPermit, expires: new DateOnly(2026, 5, 20)));

        Assert.Single(
            Compliance.Blockers(items, Today),
            blocker => blocker.Contains("expired on 20 May 2026"));
    }

    [Fact]
    public void AnOutstandingBenefitsEnrolmentDoesNotBlock()
    {
        List<EorComplianceItem> items = AllComplete();
        items.RemoveAll(item => item.Key == ComplianceKey.StatutoryBenefits);
        items.Add(Item(ComplianceKey.StatutoryBenefits, ComplianceState.Pending));

        Assert.Empty(Compliance.Blockers(items, Today));
    }

    // An absent row is not the same as nothing required: somebody has yet to
    // look at it.
    [Fact]
    public void NothingRecordedIsOutstanding()
    {
        Assert.Equal(
            ComplianceCatalogue.All.Count(requirement => requirement.BlocksWork),
            Compliance.Blockers([], Today).Count);
    }

    // ── what a new engagement starts with ──────────────────────────────────

    // The screen defaults the employment contract to Complete, so a worker
    // added a minute ago is recorded as having a signed contract nobody has
    // seen. On a register whose purpose is proving the paperwork exists, a
    // default of "done" is the one default that must not be there.
    [Fact]
    public void ANewEngagementStartsWithEverythingOutstanding()
    {
        IReadOnlyList<EorComplianceItem> starting = Compliance.Starting(
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(ComplianceCatalogue.All.Count, starting.Count);
        Assert.All(starting, item => Assert.Equal(ComplianceState.Pending, item.State));
    }

    [Fact]
    public void ANewEngagementIsNotCompliantYet()
    {
        IReadOnlyList<EorComplianceItem> starting = Compliance.Starting(
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.False(Compliance.IsFullyCompliant(starting, Today));
        Assert.NotEmpty(Compliance.Blockers(starting, Today));
    }

    [Fact]
    public void AWorkerWithEverythingInPlaceIsCompliant()
    {
        Assert.True(Compliance.IsFullyCompliant(AllComplete(), Today));
    }

    [Fact]
    public void AnExpiredRequirementMakesAWorkerNonCompliant()
    {
        List<EorComplianceItem> items = AllComplete();
        items.RemoveAll(item => item.Key == ComplianceKey.LocalId);
        items.Add(Item(ComplianceKey.LocalId, expires: Today.AddDays(-5)));

        Assert.False(Compliance.IsFullyCompliant(items, Today));
    }

    // Not blocking work, but still not compliant — the distinction the
    // register exists to draw.
    [Fact]
    public void SomethingNonBlockingStillCountsAgainstFullCompliance()
    {
        List<EorComplianceItem> items = AllComplete();
        items.RemoveAll(item => item.Key == ComplianceKey.StatutoryBenefits);
        items.Add(Item(ComplianceKey.StatutoryBenefits, ComplianceState.Pending));

        Assert.False(Compliance.IsFullyCompliant(items, Today));
        Assert.Empty(Compliance.Blockers(items, Today));
    }
}
