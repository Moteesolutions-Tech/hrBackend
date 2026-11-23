namespace Motee.Domain.Eor;

// A subtotal in one currency. Always exact, because nothing is converted to
// produce it.
public sealed record CurrencyTotal
{
    public required string Currency { get; init; }

    public required int Workers { get; init; }

    public required decimal MonthlyCost { get; init; }
}

// The figures on the EOR cards.
//
// The spend card is the one worth describing. It reads "Monthly EOR Spend —
// total cost (USD)" and sums a monthlyCostUsd that somebody typed on each
// worker, so it is a total of hand-entered conversions that never revalue. Here
// it is derived from each engagement's own dated rate, and reports what it could
// not convert rather than quietly leaving those workers out of the sum — a
// spend figure missing two workers is worse than one that says so.
public sealed record EorSummary
{
    public required int Workers { get; init; }

    public required int Providers { get; init; }

    public required int ActiveProviders { get; init; }

    public required int Countries { get; init; }

    // Exact, per currency. The honest answer to "what does this cost", and the
    // one a finance team can reconcile against a provider's invoice.
    public required IReadOnlyList<CurrencyTotal> ByCurrency { get; init; }

    // Converted, where a rate exists.
    public required decimal MonthlyCostUsd { get; init; }

    // How many of the workers above that figure actually covers. Less than
    // Workers means somebody has no rate recorded.
    public required int ConvertedWorkers { get; init; }

    // Converted at a rate older than a quarter. The figure is still shown —
    // withholding it would leave the card blank — and this is what lets the
    // screen say it is out of date instead of presenting it as current.
    public required int StaleRateWorkers { get; init; }

    public required int WorkersWithBlockers { get; init; }

    public required int ExpiringCompliance { get; init; }

    public required int ExpiredCompliance { get; init; }

    public bool CostIsComplete => ConvertedWorkers == Workers;
}

public static class EorRollups
{
    // Live engagements only, throughout.
    //
    // The screen counts workers and spend over the active ones but counts
    // countries over all of them, so "Countries of engagement" includes
    // countries the company exited years ago — three figures on one row of
    // cards describing two different populations.
    public static EorSummary Summarise(
        IReadOnlyList<EorEngagement> engagements,
        IReadOnlyList<EorProvider> providers,
        IReadOnlyDictionary<Guid, IReadOnlyList<EorComplianceItem>> compliance,
        DateOnly today)
    {
        List<EorEngagement> live = [.. engagements.Where(row => Engagements.IsLive(row, today))];

        Dictionary<Guid, EorProvider> byId = providers.ToDictionary(provider => provider.Id);

        List<CurrencyTotal> byCurrency =
        [
            .. live
                .Where(engagement => byId.ContainsKey(engagement.ProviderId))
                .GroupBy(engagement => engagement.Currency, StringComparer.OrdinalIgnoreCase)
                .Select(group => new CurrencyTotal
                {
                    Currency = group.Key.ToUpperInvariant(),
                    Workers = group.Count(),
                    MonthlyCost = group.Sum(engagement =>
                        Engagements.TotalMonthly(engagement, byId[engagement.ProviderId])),
                })
                .OrderByDescending(total => total.MonthlyCost),
        ];

        decimal usd = 0m;
        int converted = 0;
        int stale = 0;

        foreach (EorEngagement engagement in live)
        {
            if (!byId.TryGetValue(engagement.ProviderId, out EorProvider? provider))
            {
                continue;
            }

            if (Engagements.TotalUsd(engagement, provider) is not decimal amount)
            {
                continue;
            }

            usd += amount;
            converted++;

            if (Engagements.RateIsStale(engagement, today))
            {
                stale++;
            }
        }

        int blocked = 0;
        int expiring = 0;
        int expired = 0;

        foreach (EorEngagement engagement in live)
        {
            IReadOnlyList<EorComplianceItem> items =
                compliance.TryGetValue(engagement.Id, out IReadOnlyList<EorComplianceItem>? held)
                    ? held
                    : [];

            if (Compliance.Blockers(items, today).Count > 0)
            {
                blocked++;
            }

            foreach (EorComplianceItem item in items)
            {
                switch (Compliance.StandingOf(item, today))
                {
                    case ComplianceStanding.Expired:
                        expired++;
                        break;
                    case ComplianceStanding.ExpiringSoon:
                        expiring++;
                        break;
                    default:
                        break;
                }
            }
        }

        return new EorSummary
        {
            Workers = live.Count,
            Providers = providers.Count,
            ActiveProviders = providers.Count(provider =>
                provider.Status == EorProviderStatus.Active),
            Countries = live
                .Select(engagement => engagement.CountryOfEngagement.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            ByCurrency = byCurrency,
            MonthlyCostUsd = Math.Round(usd, 2, MidpointRounding.AwayFromZero),
            ConvertedWorkers = converted,
            StaleRateWorkers = stale,
            WorkersWithBlockers = blocked,
            ExpiringCompliance = expiring,
            ExpiredCompliance = expired,
        };
    }

    // How many workers one provider currently has. Counted, never stored.
    public static int WorkersFor(
        IReadOnlyList<EorEngagement> engagements,
        Guid providerId,
        DateOnly today) =>
        engagements.Count(engagement =>
            engagement.ProviderId == providerId && Engagements.IsLive(engagement, today));
}
