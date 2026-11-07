using Motee.Domain.Workforce;

namespace Motee.Application.Workforce;

// Workforce planning: turnover and hiring, derived.
//
// The screen these feed had no data source at all — its turnover table, its
// hiring table and its skills gap were three hardcoded arrays. Two of the
// three are genuinely derivable from records that already exist, and this is
// those two.
//
// Skills gap is not here. It needs a skill per employee and a category per
// skill, and neither exists anywhere in the product — the frontend's version
// is invented rows. It becomes possible once employees carry skills; until
// then an endpoint returning something would be an endpoint returning
// fiction.
public interface IWorkforceAnalyticsService
{
    // The four figures across the top of the screen.
    Task<WorkforceOverviewDto> OverviewAsync(CancellationToken cancellationToken = default);

    // Leavers per period, split by whether they chose to go. One row per
    // period per department, plus a company row per period.
    Task<IReadOnlyList<TurnoverRowDto>> TurnoverAsync(
        TurnoverQuery query,
        CancellationToken cancellationToken = default);

    // How hiring is going, per department.
    Task<IReadOnlyList<HiringRowDto>> HiringAsync(
        HiringQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record WorkforceOverviewDto
{
    public required DateOnly AsOf { get; init; }

    public required int Headcount { get; init; }

    public required decimal AverageTenureYears { get; init; }

    // Openings still to fill across every recruiting vacancy, not the number
    // of vacancies: one requisition for three engineers is three openings.
    public required int OpenVacancies { get; init; }

    // Rolling twelve months. Null where there was nobody to leave.
    public decimal? TurnoverRatePercent { get; init; }

    public required int LeaversLastTwelveMonths { get; init; }
}

public sealed record TurnoverQuery
{
    // Quarters of this year when omitted.
    public int? Year { get; init; }

    public Guid? DepartmentId { get; init; }
}

public sealed record TurnoverRowDto
{
    public required string Period { get; init; }

    // Null on the company-wide row for a period, so a reader can tell the
    // total from a department that happens to be called nothing.
    public Guid? DepartmentId { get; init; }

    public string? DepartmentName { get; init; }

    public required int AverageHeadcount { get; init; }

    public required int Leavers { get; init; }

    public required int Voluntary { get; init; }

    public required int Involuntary { get; init; }

    // Exits with no reason recorded. Reported rather than folded into either
    // side: a gap in the data is not a statement about why somebody left.
    public required int Unclassified { get; init; }

    public decimal? RatePercent { get; init; }
}

public sealed record HiringQuery
{
    public Guid? DepartmentId { get; init; }

    // How far back filled roles are counted for the time-to-fill average.
    public int Months { get; init; } = 12;
}

public sealed record HiringRowDto
{
    public required Guid DepartmentId { get; init; }

    public required string DepartmentName { get; init; }

    public required int OpenRequisitions { get; init; }

    public required int OpenVacancies { get; init; }

    // Null until something has actually been filled. A department still
    // searching has no time-to-fill, and counting days-so-far would let the
    // slowest searches improve the figure.
    public decimal? AverageDaysToFill { get; init; }

    public required int OffersExtended { get; init; }

    public required int OffersAccepted { get; init; }

    public decimal? AcceptanceRatePercent { get; init; }

    public required int FilledThisQuarter { get; init; }

    // Vacancies open long enough to have stopped being a search. Usually the
    // brief, the salary or the panel rather than the market.
    public required int StaleVacancies { get; init; }

    public required int LongestOpenDays { get; init; }
}
