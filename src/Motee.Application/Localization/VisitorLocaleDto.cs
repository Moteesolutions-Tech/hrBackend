namespace Motee.Application.Localization;

public sealed record VisitorLocaleDto
{
    // What the visitor's network says, verbatim. Null when undetectable, and may be
    // a country Motee has no jurisdiction profile for.
    public required string? DetectedCountryCode { get; init; }

    public required bool IsSupported { get; init; }

    // The toggle selection to apply — always a supported country.
    public required string CountryCode { get; init; }

    public required string Country { get; init; }

    public required string Currency { get; init; }

    public required string CurrencySymbol { get; init; }

    public required string Locale { get; init; }

    public required string Timezone { get; init; }

    public required string Source { get; init; }
}
