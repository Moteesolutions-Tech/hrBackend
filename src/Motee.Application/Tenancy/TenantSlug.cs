using System.Globalization;
using System.Text;

namespace Motee.Application.Tenancy;

public static class TenantSlug
{
    // Matches the slug column, varchar(100).
    public const int MaxLength = 100;

    private const string Fallback = "tenant";

    public static string Normalise(string? companyName)
    {
        if (string.IsNullOrWhiteSpace(companyName))
        {
            return Fallback;
        }

        string decomposed = companyName.Normalize(NormalizationForm.FormD);
        StringBuilder builder = new(decomposed.Length);
        bool lastWasSeparator = false;

        foreach (char character in decomposed)
        {
            // Diacritics survive decomposition as separate marks; dropping them
            // turns "Café" into "cafe" rather than "caf".
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            // Apostrophes join rather than split, so O'Brien becomes obrien and not
            // o-brien. Every other punctuation mark separates words.
            if (character is '\'' or '’' or '`' or '´')
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        string slug = builder.ToString().Trim('-');

        if (slug.Length > MaxLength)
        {
            slug = slug[..MaxLength].TrimEnd('-');
        }

        return slug.Length == 0 ? Fallback : slug;
    }

    // Room for "-<counter>" without exceeding the column.
    public static string WithSuffix(string slug, int counter)
    {
        string suffix = $"-{counter}";
        int available = MaxLength - suffix.Length;

        string root = slug.Length > available ? slug[..available].TrimEnd('-') : slug;

        return root + suffix;
    }
}
