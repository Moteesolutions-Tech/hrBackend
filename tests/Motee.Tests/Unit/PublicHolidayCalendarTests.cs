using Motee.Domain.Common;
using Motee.Domain.Leave;

namespace Motee.Tests.Unit;

// Computed rather than listed, so next year's calendar is not a release. These check the
// computation against dates that are a matter of public record.
public class PublicHolidayCalendarTests
{
    private static DateOnly? Find(CountryCode country, int year, string name) =>
        PublicHolidayCalendar.For(country, year)
            .Where(holiday => holiday.Name.StartsWith(name, StringComparison.Ordinal))
            .Select(holiday => (DateOnly?)holiday.Date)
            .FirstOrDefault();

    [Theory]
    [InlineData(2024, 3, 31)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    [InlineData(2038, 4, 25)]
    public void EasterIsComputedCorrectly(int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), PublicHolidayCalendar.EasterSunday(year));

    // Good Friday is two days before, Easter Monday the day after. Both countries.
    [Fact]
    public void TheEasterHolidaysHangOffEasterSunday()
    {
        Assert.Equal(new DateOnly(2026, 4, 3), Find(CountryCode.UnitedKingdom, 2026, "Good Friday"));
        Assert.Equal(new DateOnly(2026, 4, 6), Find(CountryCode.UnitedKingdom, 2026, "Easter Monday"));
        Assert.Equal(new DateOnly(2026, 4, 3), Find(CountryCode.Nigeria, 2026, "Good Friday"));
    }

    // Matches the frontend's hardcoded 2026 list, which is a useful independent check
    // that the computation agrees with what somebody looked up by hand.
    [Fact]
    public void TheUkBankHolidaysForTwentyTwentySixAreRight()
    {
        Assert.Equal(new DateOnly(2026, 1, 1), Find(CountryCode.UnitedKingdom, 2026, "New Year"));
        Assert.Equal(new DateOnly(2026, 5, 4), Find(CountryCode.UnitedKingdom, 2026, "Early May"));
        Assert.Equal(new DateOnly(2026, 5, 25), Find(CountryCode.UnitedKingdom, 2026, "Spring"));
        Assert.Equal(new DateOnly(2026, 8, 31), Find(CountryCode.UnitedKingdom, 2026, "Summer"));
        Assert.Equal(new DateOnly(2026, 12, 25), Find(CountryCode.UnitedKingdom, 2026, "Christmas"));
    }

    // 2026: Christmas is a Friday, so Boxing Day falls on Saturday and is observed on the
    // Monday. The frontend's own list says "Boxing Day (substitute)" on the 28th, which
    // is the same answer.
    [Fact]
    public void AHolidayOnAWeekendMovesToTheNextWeekday()
    {
        Assert.Equal(new DateOnly(2026, 12, 28), Find(CountryCode.UnitedKingdom, 2026, "Boxing Day"));
    }

    // 2027: Christmas is a Saturday and Boxing Day a Sunday. They must not collapse onto
    // the same Monday — that would quietly cost staff a day off.
    [Fact]
    public void TwoWeekendHolidaysInARowGetSeparateDays()
    {
        IReadOnlyList<(DateOnly Date, string Name)> holidays =
            PublicHolidayCalendar.For(CountryCode.UnitedKingdom, 2027);

        Assert.Equal(new DateOnly(2027, 12, 27), Find(CountryCode.UnitedKingdom, 2027, "Christmas"));
        Assert.Equal(new DateOnly(2027, 12, 28), Find(CountryCode.UnitedKingdom, 2027, "Boxing Day"));

        Assert.Equal(holidays.Count, holidays.Select(holiday => holiday.Date).Distinct().Count());
    }

    // Named as a substitute so a calendar can show why the office is shut on a day that
    // is not the holiday itself.
    [Fact]
    public void ASubstituteDaySaysSo()
    {
        Assert.Contains(
            PublicHolidayCalendar.For(CountryCode.UnitedKingdom, 2026),
            holiday => holiday.Name.Contains("substitute", StringComparison.Ordinal));
    }

    [Fact]
    public void NigeriaGetsItsOwnStatutoryDays()
    {
        Assert.Equal(new DateOnly(2026, 5, 1), Find(CountryCode.Nigeria, 2026, "Workers"));
        Assert.Equal(new DateOnly(2026, 6, 12), Find(CountryCode.Nigeria, 2026, "Democracy"));
        Assert.Equal(new DateOnly(2026, 10, 1), Find(CountryCode.Nigeria, 2026, "Independence"));
    }

    // The two countries genuinely differ. A shared list would be wrong for both.
    [Fact]
    public void TheTwoCountriesDoNotShareACalendar()
    {
        Assert.Null(Find(CountryCode.UnitedKingdom, 2026, "Democracy"));
        Assert.Null(Find(CountryCode.Nigeria, 2026, "Spring Bank"));
    }

    // Islamic holidays are announced by moon sighting, so they are deliberately absent
    // rather than guessed. This pins that decision: if somebody later adds a computed
    // Eid, they have to change this test and think about it.
    [Fact]
    public void MoonSightingHolidaysAreLeftForTenantsToAdd()
    {
        Assert.DoesNotContain(
            PublicHolidayCalendar.For(CountryCode.Nigeria, 2026),
            holiday => holiday.Name.Contains("Eid", StringComparison.OrdinalIgnoreCase)
                || holiday.Name.Contains("Maulud", StringComparison.OrdinalIgnoreCase));
    }

    // Every year has to produce a usable calendar — no duplicate dates, nothing outside
    // the year it was asked for.
    [Theory]
    [InlineData(2024)]
    [InlineData(2025)]
    [InlineData(2026)]
    [InlineData(2027)]
    [InlineData(2030)]
    public void EveryYearProducesACoherentCalendar(int year)
    {
        foreach (CountryCode country in new[] { CountryCode.UnitedKingdom, CountryCode.Nigeria })
        {
            IReadOnlyList<(DateOnly Date, string Name)> holidays =
                PublicHolidayCalendar.For(country, year);

            Assert.NotEmpty(holidays);

            Assert.Equal(holidays.Count, holidays.Select(holiday => holiday.Date).Distinct().Count());

            // A substituted new year can legitimately land in January of the same year
            // only; nothing should escape into a neighbouring year.
            Assert.All(holidays, holiday => Assert.Equal(year, holiday.Date.Year));

            Assert.All(holidays, holiday => Assert.False(
                holiday.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                $"{holiday.Name} on {holiday.Date} falls at a weekend."));
        }
    }
}
