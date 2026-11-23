using Motee.Domain.Common;

namespace Motee.Domain.Eor;

public enum PayFrequency
{
    Monthly,
    BiWeekly,
    Weekly,
}

// Where an engagement has got to.
//
// Derived from its dates, not stored. The screen has a status dropdown beside
// an end-date field and nothing pairs them, so a worker can be Active with an
// end date two months past, or Ended with no end date at all — and the register
// reads whichever the dropdown says.
public enum EngagementStage
{
    // Starts in the future.
    Onboarding,

    Active,

    // An end date is set and has not arrived.
    Offboarding,

    Ended,
}

// One worker engaged through one provider.
//
// Deliberately an engagement against an existing employee rather than a worker
// record of its own. The EOR screen collects title, legal names, preferred and
// maiden name, date of birth, gender, marital status, nationality, email, phone,
// role and department — and employees already holds every one of those columns.
// A second table would be a second people register: the same person's date of
// birth in two places, two directories, two org charts, and nothing keeping
// them in step. Its own data.ts says the form "mirrors the employee profile",
// which is the tell.
//
// So an EOR worker is somebody who works here whose legal employer is the
// provider. They appear in the directory, hold documents and assets, and book
// leave like anybody else; this record carries only what is true because of the
// EOR arrangement.
public class EorEngagement : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // One live engagement per person. Somebody moving between providers ends
    // one and starts another, which is a history worth having rather than two
    // rows both claiming to be current.
    public Guid EmployeeId { get; set; }

    public Guid ProviderId { get; set; }

    // Where the worker is legally employed, which is not necessarily where the
    // company is. Checked against the provider's coverage.
    public required string CountryOfEngagement { get; set; }

    // ISO code of the worker's local pay.
    //
    // The symbol is not stored alongside it: the screen keeps both and they can
    // disagree, and a code already determines a symbol.
    public required string Currency { get; set; }

    public decimal GrossMonthly { get; set; }

    // Local statutory employer costs — the provider's figure for pension,
    // social insurance and the rest.
    public decimal EmployerCostMonthly { get; set; }

    public PayFrequency PayFrequency { get; set; } = PayFrequency.Monthly;

    public DateOnly StartDate { get; set; }

    public DateOnly? EndDate { get; set; }

    // What one unit of the local currency was worth in USD, and when that was
    // taken.
    //
    // A rate with a date rather than a converted total. The screen asks for
    // "Monthly cost (USD)" as a typed number beside the local figures, so the
    // headline spend is whatever somebody entered — it never revalues when pay
    // changes, and nothing says how old it is. A dated rate makes the total
    // derived and its staleness visible.
    public decimal? UsdRate { get; set; }

    public DateOnly? RateOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public static class Engagements
{
    // Beyond this a rate is old enough that the figures built on it should not
    // be read as current. A quarter: long enough that nobody is asked to update
    // rates monthly, short enough that a year-old rate cannot sit behind a
    // spend figure unremarked.
    public const int RateStaleAfterDays = 92;

    public static EngagementStage StageOf(EorEngagement engagement, DateOnly today)
    {
        if (engagement.EndDate is DateOnly end)
        {
            return end <= today ? EngagementStage.Ended : EngagementStage.Offboarding;
        }

        return engagement.StartDate > today ? EngagementStage.Onboarding : EngagementStage.Active;
    }

    // Who is currently engaged. Every figure that counts EOR workers or their
    // cost has to agree on this, the same way EmployeeLifecycle.InPost settles
    // who is in post — otherwise the worker count and the spend describe
    // different sets of people.
    public static bool IsLive(EorEngagement engagement, DateOnly today) =>
        StageOf(engagement, today) != EngagementStage.Ended;

    // The provider's cut, worked out from the provider's own percentage.
    //
    // Not a figure typed per worker. The screen has a management fee field on
    // the worker and a management fee percentage on the provider, and nothing
    // reconciles them — so raising the provider's rate changes no worker's
    // cost, and a worker can carry a fee that matches no agreement.
    public static decimal ManagementFee(EorEngagement engagement, EorProvider provider) =>
        Round(engagement.GrossMonthly * provider.ManagementFeePercent / 100m);

    public static decimal TotalMonthly(EorEngagement engagement, EorProvider provider) =>
        Round(engagement.GrossMonthly
            + engagement.EmployerCostMonthly
            + ManagementFee(engagement, provider));

    // In USD, or null where no rate has been recorded. Null rather than zero:
    // a worker contributing nothing to the total is a worker whose cost has
    // been forgotten, and zero hides that inside a sum.
    public static decimal? TotalUsd(EorEngagement engagement, EorProvider provider) =>
        engagement.UsdRate is decimal rate && rate > 0m
            ? Round(TotalMonthly(engagement, provider) * rate)
            : null;

    public static bool RateIsStale(EorEngagement engagement, DateOnly today) =>
        engagement.RateOn is not DateOnly taken
            || today.DayNumber - taken.DayNumber > RateStaleAfterDays;

    public static EorRefusal Check(
        EorEngagement engagement,
        EorProvider provider,
        bool alreadyEngaged)
    {
        if (provider.Status != EorProviderStatus.Active)
        {
            return EorRefusal.ProviderInactive;
        }

        // The check the screen's country and provider dropdowns never make
        // against each other. Engaging somebody where the provider cannot
        // employ leaves a worker with no legal employer, and nothing about the
        // row looks wrong afterwards.
        if (!EorProviders.Covers(provider, engagement.CountryOfEngagement))
        {
            return EorRefusal.ProviderDoesNotCoverCountry;
        }

        if (alreadyEngaged)
        {
            return EorRefusal.AlreadyEngaged;
        }

        if (string.IsNullOrWhiteSpace(engagement.Currency))
        {
            return EorRefusal.CurrencyUnknown;
        }

        if (engagement.GrossMonthly <= 0m)
        {
            return EorRefusal.GrossNotPositive;
        }

        if (engagement.EmployerCostMonthly < 0m)
        {
            return EorRefusal.CostNegative;
        }

        if (engagement.EndDate is DateOnly end && end < engagement.StartDate)
        {
            return EorRefusal.EndsBeforeStart;
        }

        if (engagement.UsdRate is decimal rate)
        {
            if (rate <= 0m)
            {
                return EorRefusal.RateNotPositive;
            }

            // A rate with no date is a rate nobody can judge. The whole point
            // of holding one instead of a converted total is that its age is
            // visible.
            if (engagement.RateOn is null)
            {
                return EorRefusal.RateDateMissing;
            }
        }

        return EorRefusal.None;
    }

    private static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}
