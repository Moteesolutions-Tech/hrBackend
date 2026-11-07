using Motee.Domain.Sickness;

namespace Motee.Tests.Unit;

// The Bradford Factor: B = S² × D, where S is separate spells and D is days.
//
// Squaring the spells is the whole point — it says five one-day absences
// disrupt a team more than one five-day absence — and it also means a
// miscounted spell moves the score by a lot. A record counted as two spells
// instead of one takes somebody from 10 to 40.
//
// The worked examples below come from the client's brief and from the
// frontend's own test suite, so both implementations are answering the same
// paper. Pure arithmetic, so no database.
//
// The score is a monitoring indicator. It is never a sanction, and nothing in
// this codebase may act on it without a person in the loop.
public class BradfordFactorTests
{
    private static readonly DateOnly AsOf = new(2026, 6, 30);

    private static readonly IReadOnlySet<DayOfWeek> MondayToFriday = new HashSet<DayOfWeek>
    {
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
    };

    private static int _sequence;

    private static SicknessRecord Off(
        string start,
        string? end,
        SicknessKind kind = SicknessKind.Sickness,
        SicknessApprovalStatus status = SicknessApprovalStatus.Approved,
        DayPortion startPortion = DayPortion.Full,
        DayPortion endPortion = DayPortion.Full,
        bool overnightShift = false,
        ProtectedCategory? excluded = null)
    {
        _sequence++;

        return new SicknessRecord
        {
            Id = Guid.NewGuid(),
            EmployeeId = Employee,
            Kind = kind,
            StartDate = DateOnly.Parse(start),
            StartPortion = startPortion,
            EndDate = end is null ? null : DateOnly.Parse(end),
            EndPortion = endPortion,
            OvernightShift = overnightShift,
            ApprovalStatus = status,
            AbsenceState = end is null ? AbsenceState.Ongoing : AbsenceState.Returned,
            Reason = SicknessReason.ColdOrFlu,
            NotifiedAt = DateTimeOffset.UtcNow,
            Source = SicknessSource.Hr,
            ReportedAt = DateTimeOffset.UtcNow,
            ExclusionCategory = excluded,
            ExclusionReason = excluded is null ? null : "Pregnancy-related illness",
        };
    }

    private static readonly Guid Employee = Guid.NewGuid();

    private static BradfordResult Score(
        IReadOnlyCollection<SicknessRecord> records,
        BradfordSettings? settings = null,
        IReadOnlySet<DayOfWeek>? workingDays = null,
        IReadOnlySet<DateOnly>? holidays = null) =>
        BradfordFactor.Calculate(
            Employee,
            records,
            AsOf,
            settings ?? BradfordSettings.Default,
            workingDays ?? MondayToFriday,
            holidays ?? new HashSet<DateOnly>());

    // ── the brief's worked examples ─────────────────────────────────────────

    [Fact]
    public void OneAbsenceOfTenDaysScoresTen()
    {
        BradfordResult result = Score([Off("2026-03-02", "2026-03-13")]);

        Assert.Equal(1, result.Spells);
        Assert.Equal(10m, result.Days);
        Assert.Equal(10m, result.Score);
    }

    [Fact]
    public void TwoAbsencesOfFiveDaysScoreForty()
    {
        BradfordResult result = Score(
            [Off("2026-03-02", "2026-03-06"), Off("2026-04-13", "2026-04-17")]);

        Assert.Equal(2, result.Spells);
        Assert.Equal(10m, result.Days);
        Assert.Equal(40m, result.Score);
    }

    // The example that explains the square: the same ten days off, taken five
    // times instead of once, scores twenty-five times as much.
    [Fact]
    public void FiveAbsencesOfTwoDaysScoreTwoHundredAndFifty()
    {
        BradfordResult result = Score(
        [
            Off("2026-01-13", "2026-01-14"),
            Off("2026-02-10", "2026-02-11"),
            Off("2026-03-10", "2026-03-11"),
            Off("2026-04-14", "2026-04-15"),
            Off("2026-05-12", "2026-05-13"),
        ]);

        Assert.Equal(5, result.Spells);
        Assert.Equal(10m, result.Days);
        Assert.Equal(250m, result.Score);
    }

    [Fact]
    public void FourEpisodesOverTenDaysScoreOneHundredAndSixty() =>
        Assert.Equal(160m, Score(
        [
            Off("2026-01-12", "2026-01-14"),
            Off("2026-02-09", "2026-02-11"),
            Off("2026-03-10", "2026-03-11"),
            Off("2026-04-14", "2026-04-15"),
        ]).Score);

    // ── counting spells ─────────────────────────────────────────────────────

    // Off on the Friday and again on the Monday is one spell, not two. Counting
    // it as two would square a 1 into a 4 for a weekend nobody worked.
    [Fact]
    public void FridayAndTheFollowingMondayAreOneSpell()
    {
        BradfordResult result = Score(
            [Off("2026-03-06", "2026-03-06"), Off("2026-03-09", "2026-03-09")]);

        Assert.Equal(1, result.Spells);
        Assert.Equal(2m, result.Days);
    }

    [Fact]
    public void AnEmployerCanInsistTheWeekendBreaksTheSpell() =>
        Assert.Equal(2, Score(
            [Off("2026-03-06", "2026-03-06"), Off("2026-03-09", "2026-03-09")],
            BradfordSettings.Default with { MergeAcrossNonWorkingDays = false }).Spells);

    // A corrected absence entered twice, or two records that overlap, is one
    // absence. Counting the overlap twice inflates D and the score with it.
    [Fact]
    public void OverlappingRecordsAreNeverCountedTwice()
    {
        BradfordResult result = Score(
            [Off("2026-03-02", "2026-03-04"), Off("2026-03-03", "2026-03-05")]);

        Assert.Equal(1, result.Spells);
        Assert.Equal(4m, result.Days);
    }

    [Fact]
    public void HalfDaysCountAsHalf() =>
        Assert.Equal(1.5m, Score(
            [Off("2026-03-02", "2026-03-03", startPortion: DayPortion.Am)]).Days);

    [Fact]
    public void AnEmployerCanCountAHalfDayWhole() =>
        Assert.Equal(2m, Score(
            [Off("2026-03-02", "2026-03-03", startPortion: DayPortion.Am)],
            BradfordSettings.Default with { HalfDaysAsHalf = false }).Days);

    // A night shift belongs to the day it started, or somebody who was off for
    // one shift reads as off for two days.
    [Fact]
    public void AnOvernightShiftBelongsToTheDayItStarted() =>
        Assert.Equal(1m, Score(
            [Off("2026-03-03", "2026-03-04", overnightShift: true)]).Days);

    // ── what does not count ─────────────────────────────────────────────────

    // The Equality Act 2010 requires disability- and pregnancy-related absence
    // to be considered separately. Scoring it alongside everything else is how
    // a disabled employee reaches a formal review for being disabled.
    [Fact]
    public void AbsenceHrHasExcludedIsLeftOutWithItsReason()
    {
        BradfordResult result = Score(
        [
            Off("2026-03-02", "2026-03-06", excluded: ProtectedCategory.Pregnancy),
            Off("2026-04-13", "2026-04-14"),
        ]);

        Assert.Equal(1, result.Spells);
        Assert.Equal(2m, result.Days);
        Assert.Contains("Pregnancy", Assert.Single(result.Excluded).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmployerCanScoreExcludedAbsenceAnyway() =>
        Assert.Equal(5m, Score(
            [Off("2026-03-02", "2026-03-06", excluded: ProtectedCategory.Pregnancy)],
            BradfordSettings.Default with { HonourExclusions = false }).Days);

    // A booked operation is not unplanned absence, and a refused record is not
    // absence at all.
    [Fact]
    public void RejectedRecordsAndPlannedProceduresAreIgnored()
    {
        BradfordResult result = Score(
        [
            Off("2026-03-02", "2026-03-06", status: SicknessApprovalStatus.Rejected),
            Off("2026-04-13", "2026-04-14", kind: SicknessKind.MedicalProcedure),
        ]);

        Assert.Equal(0m, result.Score);
        Assert.Equal(2, result.Excluded.Count);
    }

    [Fact]
    public void AnAbsenceStillWaitingForApprovalCounts() =>
        Assert.Equal(5m, Score([Off(
            "2026-03-02", "2026-03-06",
            status: SicknessApprovalStatus.AwaitingApproval)]).Days);

    // ── the window ──────────────────────────────────────────────────────────

    [Fact]
    public void TheWindowIsFiftyTwoWeeksEndingToday()
    {
        BradfordResult result = Score([]);

        Assert.Equal(new DateOnly(2025, 7, 2), result.WindowStart);
        Assert.Equal(AsOf, result.WindowEnd);
    }

    // A spell that began before the window is still one spell, but only the
    // days inside the window are counted — otherwise a long absence would keep
    // scoring forever.
    [Fact]
    public void ASpellCrossingTheWindowStartCountsOnceWithOnlyItsDaysInside()
    {
        BradfordResult result = Score([Off("2025-06-23", "2025-07-04")]);

        Assert.Equal(1, result.Spells);
        Assert.Equal(3m, result.Days);
        Assert.True(Assert.Single(result.Episodes).Clipped);
    }

    [Fact]
    public void AnAbsenceEntirelyBeforeTheWindowScoresNothing() =>
        Assert.Equal(0m, Score([Off("2025-01-06", "2025-01-10")]).Score);

    [Fact]
    public void AnotherPersonsAbsenceIsNotCounted()
    {
        SicknessRecord theirs = Off("2026-03-02", "2026-03-13");
        theirs.EmployeeId = Guid.NewGuid();

        Assert.Equal(0m, Score([theirs]).Score);
    }

    // ── whose week it is ────────────────────────────────────────────────────

    [Fact]
    public void TheEmployeesOwnWorkPatternDecidesWhichDaysCount() =>
        Assert.Equal(2m, Score(
            [Off("2026-03-02", "2026-03-06")],
            workingDays: new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Wednesday }).Days);

    [Fact]
    public void PublicHolidaysAreNotDaysOffSick() =>
        Assert.Equal(2m, Score(
            [Off("2026-04-02", "2026-04-07")],
            holidays: new HashSet<DateOnly>
            {
                new(2026, 4, 3),
                new(2026, 4, 6),
            }).Days);

    // ── banding ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, BradfordAction.Monitor)]
    [InlineData(49, BradfordAction.Monitor)]
    [InlineData(50, BradfordAction.ManagerReview)]
    [InlineData(199, BradfordAction.ManagerReview)]
    [InlineData(200, BradfordAction.FormalReview)]
    [InlineData(399, BradfordAction.FormalReview)]
    [InlineData(400, BradfordAction.SeniorHrReview)]
    [InlineData(5000, BradfordAction.SeniorHrReview)]
    public void AScoreIsBandedAgainstTheEmployersThresholds(int score, BradfordAction expected) =>
        Assert.Equal(expected, BradfordSettings.Default.Band(score).Action);

    [Fact]
    public void TenDaysInOneSpellIsNormalMonitoring() =>
        Assert.Equal(
            BradfordAction.Monitor, Score([Off("2026-03-02", "2026-03-13")]).Action);

    [Fact]
    public void FiveShortAbsencesSuggestAFormalReview() =>
        Assert.Equal(BradfordAction.FormalReview, Score(
        [
            Off("2026-01-13", "2026-01-14"),
            Off("2026-02-10", "2026-02-11"),
            Off("2026-03-10", "2026-03-11"),
            Off("2026-04-14", "2026-04-15"),
            Off("2026-05-12", "2026-05-13"),
        ]).Action);

    // ── one record on its own ───────────────────────────────────────────────

    [Fact]
    public void AnOngoingAbsenceIsCountedUpToToday() =>
        Assert.Equal(2m, Absences.WorkingDaysLost(
            Off("2026-06-29", null), AsOf, MondayToFriday, new HashSet<DateOnly>()));

    [Fact]
    public void ASpellIsMeasuredInCalendarDaysInclusive()
    {
        Assert.Equal(1, Absences.SpellLength(Off("2026-03-02", "2026-03-02"), AsOf));
        Assert.Equal(12, Absences.SpellLength(Off("2026-03-02", "2026-03-13"), AsOf));
        Assert.Equal(2, Absences.SpellLength(Off("2026-06-29", null), AsOf));
    }

    // An end date before the start is somebody mistyping, not a negative
    // absence. Treated as a single day rather than throwing out the record.
    [Fact]
    public void AnEndBeforeTheStartIsTreatedAsOneDay() =>
        Assert.Equal(1, Absences.SpellLength(Off("2026-03-10", "2026-03-02"), AsOf));

    [Theory]
    [InlineData("2026-03-02", "2026-03-20", true)]
    [InlineData("2026-03-02", "2026-03-27", true)]
    [InlineData("2026-03-02", "2026-03-10", false)]
    public void LongTermAbsenceIsMeasuredInCalendarDaysNotWorkingOnes(
        string start, string end, bool expected) =>
        Assert.Equal(expected, Absences.IsLongTerm(Off(start, end), AsOf, longTermDays: 19));
}
