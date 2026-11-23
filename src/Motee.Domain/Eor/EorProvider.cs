using Motee.Domain.Common;

namespace Motee.Domain.Eor;

public enum EorProviderStatus
{
    Active,
    Inactive,
}

// A third party that becomes the legal employer of a worker in that worker's
// country, handling local payroll, tax, statutory benefits and compliance on
// the company's behalf.
//
// How many workers are engaged through one is counted from the engagements
// rather than kept here. The screen stores workerCount on the provider and the
// number sits on the card next to the list it disagrees with — the same drift
// the course enrolment counts had, and it goes wrong the first time somebody
// ends an engagement.
public class EorProvider : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public EorProviderStatus Status { get; set; } = EorProviderStatus.Active;

    // The countries this provider can legally employ in, as names matching the
    // engagement's country.
    //
    // Checked when an engagement is created, which is the point of recording
    // them: engaging somebody in a country the provider does not cover is the
    // mistake that produces a worker with no legal employer, and the screen
    // lists the countries without ever comparing them to anything.
    public List<string> CountriesCovered { get; set; } = [];

    // The provider's fee as a percentage of gross pay. One number here rather
    // than a fee typed per worker: the screen has both, and nothing applies
    // this one, so the per-worker figure can say anything.
    public decimal ManagementFeePercent { get; set; }

    public DateOnly? PartneredOn { get; set; }

    public string? Website { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum EorRefusal
{
    None,
    NameRequired,
    FeePercentOutOfRange,
    NoCountriesCovered,
    ProviderNotFound,
    ProviderInactive,
    ProviderDoesNotCoverCountry,
    ProviderHasLiveEngagements,
    EmployeeNotFound,
    AlreadyEngaged,
    EndsBeforeStart,
    GrossNotPositive,
    CostNegative,
    CurrencyUnknown,
    RateNotPositive,
    RateDateMissing,
    EndDateRequired,
    PeriodRequired,
    AmountNegative,
    InvoiceNotFound,
}

public static class EorProviders
{
    // A fee of zero is legitimate — some arrangements are a flat monthly charge
    // settled outside this product. A hundred per cent is not: it would mean the
    // provider's fee equals the worker's entire gross pay, which is a typo
    // somebody made rather than a deal somebody signed.
    public const decimal MaximumFeePercent = 60m;

    public static EorRefusal Check(string? name, decimal feePercent, IReadOnlyList<string>? countries)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return EorRefusal.NameRequired;
        }

        if (feePercent < 0m || feePercent > MaximumFeePercent)
        {
            return EorRefusal.FeePercentOutOfRange;
        }

        // A provider covering nowhere can employ nobody, so recording one is
        // recording a partner no engagement can ever name.
        return countries is null || countries.Count(Named) == 0
            ? EorRefusal.NoCountriesCovered
            : EorRefusal.None;
    }

    public static bool Covers(EorProvider provider, string? country) =>
        Named(country)
            && provider.CountriesCovered.Any(covered =>
                string.Equals(covered.Trim(), country!.Trim(), StringComparison.OrdinalIgnoreCase));

    // Tidied on the way in, so "Kenya", " kenya " and "Kenya " are one country
    // rather than three entries the coverage check then disagrees about.
    public static List<string> Tidy(IEnumerable<string>? countries)
    {
        List<string> tidied = [];

        foreach (string country in countries ?? [])
        {
            if (!Named(country))
            {
                continue;
            }

            string trimmed = country.Trim();

            if (!tidied.Any(held => string.Equals(held, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                tidied.Add(trimmed);
            }
        }

        return tidied;
    }

    private static bool Named(string? value) => !string.IsNullOrWhiteSpace(value);
}
