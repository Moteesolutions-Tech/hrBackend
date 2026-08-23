namespace Motee.Domain.Tenants;

// Headcount bands. Declaration order is meaningful: pricing tiers and feature gates
// compare bands, and the display labels sort wrongly as strings ("1000+" before
// "200–500"). Insert new bands in position rather than appending.
public enum CompanySize
{
    Micro,
    Small,
    Medium,
    Large,
    VeryLarge,
    Enterprise,
}

public static class CompanySizes
{
    // The member name persists; the label is presentation. Rewording "1–10" must not
    // rewrite every stored row.
    private static readonly IReadOnlyDictionary<CompanySize, string> Labels =
        new Dictionary<CompanySize, string>
        {
            [CompanySize.Micro] = "1–10",
            [CompanySize.Small] = "10–50",
            [CompanySize.Medium] = "50–200",
            [CompanySize.Large] = "200–500",
            [CompanySize.VeryLarge] = "500–1000",
            [CompanySize.Enterprise] = "1000+",
        };

    private static readonly IReadOnlyDictionary<string, CompanySize> ByLabel =
        Labels.ToDictionary(entry => entry.Value, entry => entry.Key, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<CompanySize> All => [.. Enum.GetValues<CompanySize>()];

    public static string ToLabel(CompanySize size) => Labels[size];

    // Accepts the stored name or the display label, so rows written before this was
    // an enum, and the CSV template, both still parse. Numbers are refused so "3"
    // cannot resolve to a band by ordinal.
    public static bool TryParse(string? value, out CompanySize size)
    {
        size = default;

        if (string.IsNullOrWhiteSpace(value) || int.TryParse(value, out _))
        {
            return false;
        }

        string trimmed = value.Trim();

        return ByLabel.TryGetValue(trimmed, out size)
            || (Enum.TryParse(trimmed, ignoreCase: true, out size) && Enum.IsDefined(size));
    }
}
