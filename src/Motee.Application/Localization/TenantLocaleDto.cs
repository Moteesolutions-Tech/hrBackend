namespace Motee.Application.Localization;

// Mirrors the frontend's LocaleTenant. Currency, symbol, locale and timezone are
// derived from the country profile rather than stored per tenant, so a jurisdiction
// change is made in one place.
public sealed record TenantLocaleDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Slug { get; init; }

    public required string Plan { get; init; }

    public required string Status { get; init; }

    public string? Industry { get; init; }

    public required string Country { get; init; }

    public required string CountryCode { get; init; }

    public required string Timezone { get; init; }

    public required string Currency { get; init; }

    public required string CurrencySymbol { get; init; }

    public required string Locale { get; init; }

    public string? LogoUrl { get; init; }

    public string? PrimaryColor { get; init; }

    public required bool UsesPayeStarterRecords { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? TrialEndsAt { get; init; }

    public string? BillingEmail { get; init; }
}
