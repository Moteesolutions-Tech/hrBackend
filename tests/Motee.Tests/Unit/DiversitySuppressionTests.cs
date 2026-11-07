using Motee.Domain.Common;
using Motee.Domain.Diversity;

namespace Motee.Tests.Unit;

// Diversity reporting, and the suppression that makes it safe to publish.
//
// This is special-category data under UK GDPR and sensitive personal data
// under Nigeria's NDPA. The rule that matters is not the tally, it is what the
// tally refuses to say: a group of two on an ethnicity chart, in a company
// where the reader knows the team, names those two people by elimination.
//
// Five is the ONS threshold and the one most HR analytics guidance uses.
public class DiversitySuppressionTests
{
    private static string[] Answers(params (string Value, int Count)[] groups) =>
        [.. groups.SelectMany(group => Enumerable.Repeat(group.Value, group.Count))];

    [Fact]
    public void AGroupAtTheThresholdIsReported()
    {
        SuppressedBreakdown breakdown = Suppression.Tally(
            Answers(("White", 5), ("Asian or Asian British", 5)), eligible: 10);

        Assert.Equal(2, breakdown.Rows.Count);
        Assert.Equal(0, breakdown.SuppressedCount);
        Assert.Equal(10, breakdown.Declared);
    }

    // The whole point. Four people is a group small enough to identify.
    [Fact]
    public void AGroupBelowTheThresholdIsFoldedAway()
    {
        SuppressedBreakdown breakdown = Suppression.Tally(
            Answers(("White", 20), ("Mixed or multiple ethnic groups", 4)), eligible: 24);

        Assert.Equal("White", Assert.Single(breakdown.Rows).Label);
        Assert.Equal(4, breakdown.SuppressedCount);
        Assert.Equal(1, breakdown.SuppressedGroups);
    }

    [Fact]
    public void SeveralSmallGroupsAreFoldedIntoOneFigure()
    {
        SuppressedBreakdown breakdown = Suppression.Tally(
            Answers(("White", 20), ("Mixed or multiple ethnic groups", 3), ("Other ethnic group", 2)),
            eligible: 25);

        Assert.Single(breakdown.Rows);
        Assert.Equal(5, breakdown.SuppressedCount);
        Assert.Equal(2, breakdown.SuppressedGroups);
    }

    // A near-empty chart would read as "we have no data" when the truth is
    // "we are protecting it". Withholding the lot says the second thing.
    [Fact]
    public void ABreakdownWhereNothingCouldBeReportedIsWithheldEntirely()
    {
        SuppressedBreakdown breakdown = Suppression.Tally(
            Answers(("White", 4), ("Asian or Asian British", 3)), eligible: 7);

        Assert.Empty(breakdown.Rows);
        Assert.True(breakdown.FullySuppressed);
        Assert.Equal(7, breakdown.SuppressedCount);
    }

    [Fact]
    public void ABreakdownWithSomethingToSayIsNotWithheld() =>
        Assert.False(Suppression.Tally(
            Answers(("White", 6), ("Other ethnic group", 2)), eligible: 8).FullySuppressed);

    // ── the denominator ────────────────────────────────────────────────────

    // Percentages are of those who answered, not of the headcount. A company
    // where half the staff declared would otherwise halve every figure and
    // look far less diverse than it is.
    [Fact]
    public void PercentagesAreOfThoseWhoAnswered()
    {
        SuppressedBreakdown breakdown = Suppression.Tally(
            Answers(("White", 6), ("Asian or Asian British", 6)), eligible: 100);

        Assert.Equal(50, Assert.Single(breakdown.Rows, row => row.Label == "White").Percentage);
        Assert.Equal(12, breakdown.Declared);
        Assert.Equal(100, breakdown.Eligible);
    }

    [Fact]
    public void PeopleWhoDidNotAnswerAreNotCountedAsAnAnswer()
    {
        SuppressedBreakdown breakdown = Suppression.Tally(
            [.. Answers(("White", 6)), null, null, null], eligible: 9);

        Assert.Equal(6, breakdown.Declared);
        Assert.Equal(100, Assert.Single(breakdown.Rows).Percentage);
    }

    // Declining is an answer, and a real one. Reporting how many people chose
    // not to say is part of reporting honestly.
    [Fact]
    public void PreferringNotToSayIsAnAnswerLikeAnyOther()
    {
        SuppressedBreakdown breakdown = Suppression.Tally(
            Answers(("White", 6), (Diversities.PreferNotToSay, 6)), eligible: 12);

        Assert.Contains(breakdown.Rows, row => row.Label == Diversities.PreferNotToSay);
    }

    [Fact]
    public void NobodyAnsweringIsNotADivideByZero()
    {
        SuppressedBreakdown breakdown = Suppression.Tally([], eligible: 40);

        Assert.Empty(breakdown.Rows);
        Assert.Equal(0, breakdown.Declared);
        Assert.Equal(0, Suppression.DeclarationRate(0, 40));
        Assert.Equal(0, Suppression.DeclarationRate(5, 0));
    }

    [Fact]
    public void TheDeclarationRateIsTheDatasOwnHealth() =>
        Assert.Equal(63, Suppression.DeclarationRate(25, 40));

    [Fact]
    public void BiggestGroupFirst()
    {
        SuppressedBreakdown breakdown = Suppression.Tally(
            Answers(("Christian", 6), ("No religion", 14), ("Muslim", 9)), eligible: 29);

        Assert.Equal(
            ["No religion", "Muslim", "Christian"],
            [.. breakdown.Rows.Select(row => row.Label)]);
    }

    // ── the question set ───────────────────────────────────────────────────

    [Fact]
    public void TheUkAsksAboutEthnicity() =>
        Assert.Contains(
            Diversities.CategoriesFor(CountryCode.UnitedKingdom),
            category => category.Field == DiversityField.Ethnicity);

    // Not an oversight. Ethnicity by tribe carries real discrimination risk in
    // Nigeria and is not a reporting requirement there, so it is not asked —
    // and because it is not asked, it cannot be stored.
    [Fact]
    public void NigeriaDoesNotAskAboutEthnicity() =>
        Assert.DoesNotContain(
            Diversities.CategoriesFor(CountryCode.Nigeria),
            category => category.Field == DiversityField.Ethnicity);

    [Fact]
    public void EveryCategoryOffersPreferNotToSay() =>
        Assert.All(
            Diversities.CategoriesFor(CountryCode.UnitedKingdom)
                .Concat(Diversities.CategoriesFor(CountryCode.Nigeria)),
            category => Assert.Contains(Diversities.PreferNotToSay, category.Options));

    // Every category explains why it is being asked, to the person being
    // asked, rather than leaving it buried in a policy document.
    [Fact]
    public void EveryCategorySaysWhyItIsAsked() =>
        Assert.All(
            Diversities.CategoriesFor(CountryCode.UnitedKingdom),
            category => Assert.False(string.IsNullOrWhiteSpace(category.Purpose)));

    // ── what may be stored ─────────────────────────────────────────────────

    [Fact]
    public void AnAnswerFromTheListIsAccepted() =>
        Assert.True(Diversities.IsOffered(
            CountryCode.UnitedKingdom, DiversityField.Religion, "Muslim"));

    [Fact]
    public void AnAnswerNobodyWasOfferedIsRefused() =>
        Assert.False(Diversities.IsOffered(
            CountryCode.UnitedKingdom, DiversityField.Religion, "Jedi"));

    // The guarantee a frontend cannot make: a client sending an ethnicity for
    // a Nigerian tenant is refused by the server, not merely unasked by the
    // form.
    [Fact]
    public void AnAnswerToAQuestionThisCountryDoesNotAskIsRefused() =>
        Assert.False(Diversities.IsOffered(
            CountryCode.Nigeria, DiversityField.Ethnicity, "White"));

    [Fact]
    public void TheQuestionSetsDifferByCountry() =>
        Assert.NotEqual(
            Diversities.CategoriesFor(CountryCode.Nigeria)
                .First(category => category.Field == DiversityField.Religion).Options,
            Diversities.CategoriesFor(CountryCode.UnitedKingdom)
                .First(category => category.Field == DiversityField.Religion).Options);
}
