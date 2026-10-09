using Motee.Domain.Leave;

namespace Motee.Tests.Unit;

// Which twelve months somebody's leave counts against. Getting this wrong does not fail
// loudly — it attributes leave to the wrong year, and the balance is quietly wrong.
public class LeaveYearTests
{
    [Fact]
    public void ACalendarYearRunsJanuaryToDecember()
    {
        LeaveYear year = LeaveYear.Containing(new DateOnly(2026, 6, 15), 1, 1);

        Assert.Equal(new DateOnly(2026, 1, 1), year.Start);
        Assert.Equal(new DateOnly(2026, 12, 31), year.End);
        Assert.Equal("2026", year.Label);
    }

    // The subtlety worth testing: a date before this year's turnover belongs to the year
    // that started last April, not the one starting next April.
    [Fact]
    public void ADateBeforeTheTurnoverBelongsToThePreviousYear()
    {
        LeaveYear year = LeaveYear.Containing(new DateOnly(2026, 2, 10), 4, 1);

        Assert.Equal(new DateOnly(2025, 4, 1), year.Start);
        Assert.Equal(new DateOnly(2026, 3, 31), year.End);
    }

    [Fact]
    public void ADateOnTheTurnoverStartsTheNewYear()
    {
        LeaveYear year = LeaveYear.Containing(new DateOnly(2026, 4, 1), 4, 1);

        Assert.Equal(new DateOnly(2026, 4, 1), year.Start);
        Assert.Equal(new DateOnly(2027, 3, 31), year.End);
    }

    // "2026" would be ambiguous for an April start — it could mean either half.
    [Fact]
    public void AStraddlingYearIsLabelledWithBoth()
    {
        Assert.Equal("2025/26", LeaveYear.Containing(new DateOnly(2026, 2, 10), 4, 1).Label);
    }

    // 29 February is a legal turnover day and does not exist three years in four.
    // Throwing on a date somebody entered in good faith would take the module down.
    [Fact]
    public void ALeapDayTurnoverSurvivesANonLeapYear()
    {
        LeaveYear year = LeaveYear.Containing(new DateOnly(2027, 6, 1), 2, 29);

        Assert.Equal(new DateOnly(2027, 2, 28), year.Start);
    }

    // No gaps and no overlaps: every day of a year belongs to exactly one leave year.
    [Fact]
    public void ConsecutiveYearsMeetWithoutAGap()
    {
        LeaveYear first = LeaveYear.Containing(new DateOnly(2026, 5, 1), 4, 1);
        LeaveYear next = LeaveYear.Containing(first.End.AddDays(1), 4, 1);

        Assert.Equal(first.End.AddDays(1), next.Start);
        Assert.True(first.Contains(first.End));
        Assert.False(first.Contains(next.Start));
    }
}
