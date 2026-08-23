using Motee.Domain.Common;

namespace Motee.Domain.Tenants;

public class Tenant
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string Slug { get; set; }

    public string Plan { get; set; } = "starter";

    public string Status { get; set; } = "trial";

    public string? Industry { get; set; }

    // Null until setup. Stored as the member name, not the label.
    public CompanySize? CompanySize { get; set; }

    // The domain a tenant's staff addresses share, e.g. "acme.com".
    public string? CompanyEmailDomain { get; set; }

    public string? CompanyPolicies { get; set; }

    // Wizard choices that nothing queries by field. Read and written whole.
    public TenantSettings Settings { get; set; } = new();

    // Jurisdiction. Drives currency, tax and statutory rules for this tenant's
    // employees unless an individual employee is engaged in another country.
    public required CountryCode CountryCode { get; set; }

    public string? LogoUrl { get; set; }

    public string? PrimaryColor { get; set; }

    public string? BillingEmail { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? TrialEndsAt { get; set; }

    // Null until the setup wizard is submitted. Drives whether a returning admin is
    // routed into onboarding or straight to the app.
    public DateTimeOffset? OnboardingCompletedAt { get; set; }
}
