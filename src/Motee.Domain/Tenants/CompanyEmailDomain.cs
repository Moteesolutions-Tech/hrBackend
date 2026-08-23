using System.Text.RegularExpressions;

namespace Motee.Domain.Tenants;

// The domain a tenant's staff addresses share. Only the shape is checked — whether
// that is a company's own domain or a shared provider is the tenant's choice, not
// something the platform rules on.
public static partial class CompanyEmailDomain
{
    public static bool IsValid(string? domain) =>
        !string.IsNullOrWhiteSpace(domain) && DomainPattern().IsMatch(domain.Trim());

    // Labels of letters/digits/hyphens, at least one dot, and a two-letter-or-longer
    // final label. Deliberately not a full public-suffix check.
    [GeneratedRegex(@"^(?!-)[a-z0-9-]+(?<!-)(\.(?!-)[a-z0-9-]+(?<!-))*\.[a-z]{2,}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();
}
