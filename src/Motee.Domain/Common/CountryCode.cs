namespace Motee.Domain.Common;

// ISO 3166-1 alpha-2. Note the frontend's CountryKey union uses "uk" as a bundle
// selector; the United Kingdom's actual ISO code is "GB" and that is what persists.
public readonly record struct CountryCode
{
    public static readonly CountryCode Nigeria = new("NG");
    public static readonly CountryCode UnitedKingdom = new("GB");

    private CountryCode(string value) => Value = value;

    public string Value { get; }

    public static CountryCode Parse(string? value)
    {
        if (!TryParse(value, out CountryCode code))
        {
            throw new ArgumentException($"Unsupported country code '{value}'.", nameof(value));
        }

        return code;
    }

    public static bool TryParse(string? value, out CountryCode code)
    {
        string normalised = value?.Trim().ToUpperInvariant() ?? string.Empty;

        // Tolerate the frontend's "uk" so a stray bundle key never persists as invalid.
        if (normalised == "UK")
        {
            normalised = "GB";
        }

        switch (normalised)
        {
            case "NG":
                code = Nigeria;
                return true;
            case "GB":
                code = UnitedKingdom;
                return true;
            default:
                code = default;
                return false;
        }
    }

    public override string ToString() => Value;
}
