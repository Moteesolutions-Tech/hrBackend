using System.Globalization;
using Motee.Domain.Common;

namespace Motee.Domain.Headcount;

public enum GapStatus
{
    Under,
    OnTarget,
    Over,
}

// How far recruitment has got towards closing a department's gap. Derived
// from the requisitions and candidates behind it rather than set by hand, so
// a stage cannot disagree with the pipeline it describes.
public enum RecruitmentPipelineStage
{
    NotStarted,
    RequestRaised,
    RequisitionApproved,
    VacancyPublished,
    Interviewing,
    OfferMade,
    OfferAccepted,
}

// What HR committed to for a department in a period.
//
// The only stored row in the module. Actual headcount, the gap, the open
// vacancies, how far recruitment has got and last period's gap are all
// derived — the frontend stores the actual alongside the target, which is why
// its trend tab shows numbers that were true on the day somebody saved them.
public class HeadcountPlan : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DepartmentId { get; set; }

    // "Q3 2026" or "FY 2026", as the frontend writes them. Free text behind a
    // validator rather than an enum: the enum would need a release to accept
    // 2027, and a planning tool that cannot plan next year is not one.
    public required string Period { get; set; }

    public int Target { get; set; }

    // What Finance signed off, where they have. Null means nobody has costed
    // this department's plan, which is different from a budget of nothing.
    public decimal? Budget { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// A stretch somebody was employed for. Start null means they have not
// started; end null means they have not left.
public readonly record struct Spell(DateOnly? Start, DateOnly? End);

// "Q3 2026", "FY 2026", and the calendar behind them.
public static class PlanPeriods
{
    public static bool IsValid(string? period) =>
        Quarter(period) is not null || Year(period) is not null;

    public static (int Quarter, int Year)? Quarter(string? period)
    {
        if (period is null || period.Length != 7 || period[0] != 'Q' || period[2] != ' ')
        {
            return null;
        }

        if (period[1] is < '1' or > '4')
        {
            return null;
        }

        return int.TryParse(
            period[3..], NumberStyles.None, CultureInfo.InvariantCulture, out int year)
            ? (period[1] - '0', year)
            : null;
    }

    // The year of a full-year period. Null for a quarter, so the two are
    // never confused by a caller that only checks one.
    public static int? Year(string? period)
    {
        if (period is null || period.Length != 7 || !period.StartsWith("FY ", StringComparison.Ordinal))
        {
            return null;
        }

        return int.TryParse(
            period[3..], NumberStyles.None, CultureInfo.InvariantCulture, out int year)
            ? year
            : null;
    }

    public static (DateOnly Start, DateOnly End)? Window(string? period)
    {
        if (Quarter(period) is (int quarter, int quarterYear))
        {
            DateOnly start = new(quarterYear, ((quarter - 1) * 3) + 1, 1);

            return (start, start.AddMonths(3).AddDays(-1));
        }

        return Year(period) is int year
            ? (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31))
            : null;
    }

    // Null for a full year, which has no previous quarter — the name is the
    // rule. A year's comparison period is the year before, which is what
    // SameQuarterLastYear returns for it.
    public static string? PreviousQuarter(string? period)
    {
        if (Quarter(period) is not (int quarter, int year))
        {
            return null;
        }

        // The wraparound. Q1's predecessor is last year's Q4, and reading it
        // as "Q0" or as Q4 of the same year turns a year's growth into a
        // quarter's.
        return quarter == 1 ? $"Q4 {year - 1}" : $"Q{quarter - 1} {year}";
    }

    public static string? SameQuarterLastYear(string? period)
    {
        if (Quarter(period) is (int quarter, int year))
        {
            return $"Q{quarter} {year - 1}";
        }

        return Year(period) is int full ? $"FY {full - 1}" : null;
    }

    public static string QuarterOf(DateOnly day) =>
        $"Q{((day.Month - 1) / 3) + 1} {day.Year}";

    public static IReadOnlyList<string> QuartersOf(int year) =>
        [$"Q1 {year}", $"Q2 {year}", $"Q3 {year}", $"Q4 {year}"];
}

public static class Headcounts
{
    public static GapStatus Gap(int actual, int target) => actual == target
        ? GapStatus.OnTarget
        : actual > target ? GapStatus.Over : GapStatus.Under;

    // Null rather than infinity when there was nobody to grow from. Going from
    // 0 to 5 has no meaningful percentage, and a screen showing "∞%" or
    // "500%" is reporting a division, not a fact.
    public static decimal? PercentChange(int current, int previous) => previous == 0
        ? null
        : Math.Round((current - previous) / (decimal)previous * 100m, 1, MidpointRounding.AwayFromZero);

    // What the gap report tells the reader to do, rather than leaving them to
    // infer it. Ordered: hiring under way beats an unfilled vacancy, which
    // beats having raised nothing at all.
    public static string RecommendedAction(
        int actual,
        int target,
        int activeRecruitments,
        int openVacancies) => Gap(actual, target) switch
        {
            GapStatus.Over => "Review Organisation Structure",
            GapStatus.OnTarget => "No Action Required",
            _ when activeRecruitments > 0 => "Recruitment in Progress",
            _ when openVacancies > 0 => "Fill Existing Vacancy",
            _ => "Create Workforce Request",
        };

    // Who had started and not yet left. The figure the frontend cannot
    // produce — it holds only today's roster, so it fakes past quarters with a
    // fixed decay — and the reason a past period here reports what was true.
    public static int OnDay(IReadOnlyCollection<Spell> spells, DateOnly day) =>
        spells.Count(spell =>
            spell.Start is DateOnly start
            && start <= day

            // Their last day is a day they were still here.
            && (spell.End is not DateOnly end || end >= day));

    // ── reporting bands ─────────────────────────────────────────────────────

    // Bands, never an age. A date of birth on a report is personal data that
    // the report does not need; the band is what anybody is actually asking.
    public static string AgeBand(DateOnly born, DateOnly asOf) => Age(born, asOf) switch
    {
        < 25 => "Under 25",
        < 35 => "25–34",
        < 45 => "35–44",
        < 55 => "45–54",
        < 65 => "55–64",
        _ => "65+",
    };

    // Whole years, counted off the birthday rather than by dividing days.
    // Dividing by 365.25 puts somebody a few days short of a band boundary on
    // the wrong side of it one year in four.
    public static int Age(DateOnly born, DateOnly asOf)
    {
        int age = asOf.Year - born.Year;

        return AnnualDates.OnYear(born, asOf.Year) > asOf ? age - 1 : age;
    }

    public static decimal TenureYears(DateOnly started, DateOnly asOf) =>
        Math.Round(
            (asOf.DayNumber - started.DayNumber) / 365.25m, 1, MidpointRounding.AwayFromZero);

    public static string TenureBand(DateOnly started, DateOnly asOf) =>
        ((asOf.DayNumber - started.DayNumber) / 365.25m) switch
        {
            < 1m => "<1y",
            < 3m => "1-3y",
            < 5m => "3-5y",
            _ => "5+y",
        };
}
