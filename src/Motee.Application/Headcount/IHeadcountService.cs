using Motee.Domain.Headcount;

namespace Motee.Application.Headcount;


public interface IHeadcountService
{

    Task<HeadcountPlanBoardDto> BoardAsync(
        string? period = null,
        CancellationToken cancellationToken = default);

    // Movement against the previous quarter, or the same quarter last year.
    Task<HeadcountTrendDto> TrendAsync(
        string period,
        TrendComparison comparison = TrendComparison.PreviousQuarter,
        CancellationToken cancellationToken = default);

    // Actual against target across a year's quarters, for the chart.
    Task<IReadOnlyList<HeadcountSeriesPointDto>> SeriesAsync(
        int? year = null,
        CancellationToken cancellationToken = default);

    // Workforce composition: the breakdowns the D&I and planning tabs show.
    Task<DemographicsDto> DemographicsAsync(CancellationToken cancellationToken = default);

    // Who might leave, with the factors behind each score.
    Task<IReadOnlyList<AttritionRiskDto>> AttritionAsync(
        AttritionQuery query,
        CancellationToken cancellationToken = default);

    // Set or change a department's target for a period. One row per
    // department per period, so this is an upsert rather than an insert —
    // two targets for the same quarter is two answers to one question.
    Task<HeadcountPlanResult> SetTargetAsync(
        HeadcountPlanRequest request,
        CancellationToken cancellationToken = default);

    Task<HeadcountPlanOutcome> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

public enum TrendComparison
{
    PreviousQuarter,
    SameQuarterLastYear,
}

public sealed record HeadcountPlanBoardDto
{
    public required string Period { get; init; }

    // The last day of the period, which is the day the actuals are counted
    // as at. A future period counts as at today instead — a target for next
    // quarter against a headcount nobody has yet would read as a total
    // shortfall.
    public required DateOnly CountedAsAt { get; init; }

    public required IReadOnlyList<HeadcountPlanRowDto> Rows { get; init; }

    public required int TotalTarget { get; init; }

    public required int TotalActual { get; init; }

    public required int TotalGap { get; init; }

    public required int DepartmentsUnder { get; init; }

    public required int DepartmentsOver { get; init; }

    public required int DepartmentsOnTarget { get; init; }

    // Departments with no target set for this period. Reported rather than
    // defaulted to their current headcount: "nobody has planned this" and
    // "planned to stand still" are different facts, and guessing the second
    // hides the first.
    public required int DepartmentsUnplanned { get; init; }

    public decimal? TotalBudget { get; init; }
}

public sealed record HeadcountPlanRowDto
{
    // Null where no target has been set for this department and period.
    public Guid? Id { get; init; }

    public required Guid DepartmentId { get; init; }

    public required string DepartmentName { get; init; }

    public required string Period { get; init; }

    // Null when unplanned, so the screen can say so rather than show a zero
    // that looks like a target of none.
    public int? Target { get; init; }

    public required int Actual { get; init; }

    // Actual − target. Negative means short of plan. Null when unplanned.
    public int? Gap { get; init; }

    public GapStatus? GapStatus { get; init; }

    public decimal? Budget { get; init; }

    // ── recruitment context: why a gap is still open ────────────────────────

    public required int OpenVacancies { get; init; }

    public required int ActiveRecruitments { get; init; }

    public required RecruitmentPipelineStage RecruitmentStage { get; init; }

    // Hires who have accepted and not yet started. The part of the gap that
    // is already solved and just has not arrived.
    public required int AcceptedNotStarted { get; init; }

    public DateOnly? ExpectedStartDate { get; init; }

    // The same department's gap last period, so a persistent shortfall is
    // distinguishable from a new one.
    public int? PreviousPeriodGap { get; init; }

    // What the gap report tells the reader to do, rather than leaving them to
    // infer it.
    public required string RecommendedAction { get; init; }
}

public sealed record HeadcountTrendDto
{
    public required string Period { get; init; }

    // Null when nothing is held to compare against — a full year has no
    // previous quarter, and the first period on record has no predecessor.
    public string? ComparisonPeriod { get; init; }

    public required IReadOnlyList<HeadcountTrendRowDto> Rows { get; init; }

    public required int TotalCurrent { get; init; }

    public required int TotalPrevious { get; init; }

    public required int TotalDelta { get; init; }

    public decimal? TotalPercentChange { get; init; }

    public required int Grew { get; init; }

    public required int Shrank { get; init; }

    public required int Flat { get; init; }
}

public sealed record HeadcountTrendRowDto
{
    public required Guid DepartmentId { get; init; }

    public required string DepartmentName { get; init; }

    public required int Current { get; init; }

    public required int Previous { get; init; }

    public required int Delta { get; init; }

    // Null where there was nobody to grow from. Going from 0 to 5 has no
    // meaningful percentage.
    public decimal? PercentChange { get; init; }

    public int? Target { get; init; }
}

public sealed record HeadcountSeriesPointDto
{
    public required string Period { get; init; }

    public required int Actual { get; init; }

    public int? Target { get; init; }
}

public sealed record DemographicsDto
{
    public required DateOnly AsOf { get; init; }

    public required int Headcount { get; init; }

    public required IReadOnlyList<DemographicItemDto> Department { get; init; }

    public required IReadOnlyList<DemographicItemDto> EmploymentType { get; init; }

    public required IReadOnlyList<DemographicItemDto> Status { get; init; }

    public required IReadOnlyList<DemographicItemDto> Tenure { get; init; }

    // Bands, never ages. A date of birth on a report is personal data the
    // report does not need.
    public required IReadOnlyList<DemographicItemDto> Age { get; init; }

    public required IReadOnlyList<DemographicItemDto> Gender { get; init; }

    public required IReadOnlyList<DemographicItemDto> Grade { get; init; }

    public required IReadOnlyList<DemographicItemDto> Location { get; init; }
}

public sealed record DemographicItemDto
{
    public required string Label { get; init; }

    public required int Count { get; init; }

    public required decimal Percentage { get; init; }

    // Set where the segment maps to a filter on the employee list, so a
    // reader can get from a bar to the people in it.
    public Guid? DepartmentId { get; init; }

    public Guid? BranchId { get; init; }
}

public sealed record AttritionRiskDto
{
    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? JobTitle { get; init; }

    public string? DepartmentName { get; init; }

    public string? ManagerName { get; init; }

    public required decimal TenureYears { get; init; }

    public required int Score { get; init; }

    public required AttritionRiskLevel Level { get; init; }

    // The weighted factors behind the score. Returned so somebody shown a
    // number can see what built it and say it is wrong.
    public required IReadOnlyList<RiskFactor> Factors { get; init; }

    public required string RecommendedAction { get; init; }

    public int? LastPerformanceRating { get; init; }

    public decimal? BradfordScore { get; init; }
}

public sealed record AttritionQuery
{
    public Guid? DepartmentId { get; init; }

    public AttritionRiskLevel? AtLeast { get; init; }

    public int Limit { get; init; } = 50;
}

public sealed record HeadcountPlanRequest
{
    public required Guid DepartmentId { get; init; }

    public required string Period { get; init; }

    public required int Target { get; init; }

    public decimal? Budget { get; init; }
}

public enum HeadcountPlanOutcome
{
    Succeeded,
    NotFound,
    DepartmentNotFound,

    // Not "Q3 2026" or "FY 2026".
    PeriodNotRecognised,

    // A target below zero is not a plan.
    TargetNegative,

    BudgetNegative,
}

public sealed record HeadcountPlanResult
{
    public required HeadcountPlanOutcome Outcome { get; init; }

    public HeadcountPlanRowDto? Plan { get; init; }

    public bool Succeeded => Outcome == HeadcountPlanOutcome.Succeeded;

    public static HeadcountPlanResult Failed(HeadcountPlanOutcome outcome) =>
        new() { Outcome = outcome };

    public static HeadcountPlanResult Ok(HeadcountPlanRowDto plan) =>
        new() { Outcome = HeadcountPlanOutcome.Succeeded, Plan = plan };
}
