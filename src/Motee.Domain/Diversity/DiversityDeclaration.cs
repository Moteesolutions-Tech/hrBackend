using Motee.Domain.Common;

namespace Motee.Domain.Diversity;

// What may be asked. Each field is a protected characteristic somewhere, and
// the correct question genuinely differs by jurisdiction — which is why the
// catalogue is per country rather than one list with flags.
public enum DiversityField
{
    Ethnicity,
    Disability,
    Religion,
    VeteranStatus,
    CaringResponsibilities,
}

public sealed record DiversityCategory
{
    public required DiversityField Field { get; init; }

    public required string Label { get; init; }

    // Shown to the person being asked, not buried in a policy document.
    // Somebody handing over their religion is owed a sentence about why.
    public required string Purpose { get; init; }

    public required IReadOnlyList<string> Options { get; init; }
}

// One person's self-declared answers.
//
// Three rules hold here and none of them is negotiable:
//
//   Self-declared only. There is no route that writes somebody else's
//   declaration. An employee's ethnicity is not an administrative field, and
//   a service that lets HR set it has already lost the argument.
//
//   Always optional. Every category offers "prefer not to say", and declining
//   is recorded as an answer rather than as an absence.
//
//   Aggregate-only reporting. Nothing joins a declaration back to a named
//   person, and groups below the suppression threshold are folded away before
//   anything leaves the server.
public class DiversityDeclaration : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // One row per person. Their own, written by them.
    public Guid EmployeeId { get; set; }

    public string? Ethnicity { get; set; }

    public string? Disability { get; set; }

    public string? Religion { get; set; }

    public string? VeteranStatus { get; set; }

    public string? CaringResponsibilities { get; set; }

    // Which country's question set they answered. Kept because the options
    // differ, so a stored answer is only interpretable against the set it
    // came from.
    public CountryCode Jurisdiction { get; set; }

    public DateTimeOffset DeclaredAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public static class Diversities
{
    // The answer everybody is entitled to give, in every category.
    public const string PreferNotToSay = "Prefer not to say";

    // ONS 2021 high-level ethnic groups, as used for UK statutory reporting.
    private static readonly string[] UkEthnicity =
    [
        "Asian or Asian British",
        "Black, Black British, Caribbean or African",
        "Mixed or multiple ethnic groups",
        "White",
        "Other ethnic group",
        PreferNotToSay,
    ];

    private static readonly string[] Disability =
    [
        "No",
        "Yes",
        "Yes — but I do not consider myself disabled",
        PreferNotToSay,
    ];

    private static readonly string[] UkReligion =
    [
        "No religion",
        "Buddhist",
        "Christian",
        "Hindu",
        "Jewish",
        "Muslim",
        "Sikh",
        "Other religion",
        PreferNotToSay,
    ];

    private static readonly string[] NigeriaReligion =
    [
        "Christianity",
        "Islam",
        "Traditional religion",
        "No religion",
        "Other",
        PreferNotToSay,
    ];

    private static readonly string[] Caring =
    [
        "No",
        "Yes — children",
        "Yes — an adult",
        "Yes — both",
        PreferNotToSay,
    ];

    private static readonly DiversityCategory[] UnitedKingdom =
    [
        new()
        {
            Field = DiversityField.Ethnicity,
            Label = "Ethnic group",
            Purpose = "Reported in aggregate only, to check our hiring and "
                + "progression are fair across groups.",
            Options = UkEthnicity,
        },
        new()
        {
            Field = DiversityField.Disability,
            Label = "Do you consider yourself to have a disability?",
            Purpose = "Helps us meet our duty to make reasonable adjustments "
                + "and monitor representation.",
            Options = Disability,
        },
        new()
        {
            Field = DiversityField.Religion,
            Label = "Religion or belief",
            Purpose = "Aggregate monitoring under the Equality Act 2010.",
            Options = UkReligion,
        },
        new()
        {
            Field = DiversityField.VeteranStatus,
            Label = "Have you served in the UK Armed Forces?",
            Purpose = "So we can honour our Armed Forces Covenant commitments "
                + "and offer relevant support.",
            Options =
            [
                "No",
                "Yes — I am a veteran",
                "Yes — I am currently a reservist",
                PreferNotToSay,
            ],
        },
        new()
        {
            Field = DiversityField.CaringResponsibilities,
            Label = "Do you have caring responsibilities?",
            Purpose = "Helps us shape flexible working and support policies.",
            Options = Caring,
        },
    ];

    // Ethnicity by tribe is deliberately absent. It carries real
    // discrimination risk in Nigeria and is not a reporting requirement
    // there, and collecting data you cannot protect is worse than not asking.
    // Because it is not offered, IsOffered refuses it — so it cannot be
    // stored by a client that asks anyway.
    private static readonly DiversityCategory[] Nigeria =
    [
        new()
        {
            Field = DiversityField.Disability,
            Label = "Do you consider yourself to have a disability?",
            Purpose = "Helps us make workplace adjustments under the "
                + "Discrimination Against Persons with Disabilities "
                + "(Prohibition) Act.",
            Options = Disability,
        },
        new()
        {
            Field = DiversityField.Religion,
            Label = "Religion",
            Purpose = "Aggregate monitoring only, and used to plan around "
                + "religious observances.",
            Options = NigeriaReligion,
        },
        new()
        {
            Field = DiversityField.CaringResponsibilities,
            Label = "Do you have caring responsibilities?",
            Purpose = "Helps us shape flexible working and support policies.",
            Options = Caring,
        },
    ];

    public static IReadOnlyList<DiversityCategory> CategoriesFor(CountryCode country) =>
        country == CountryCode.Nigeria ? Nigeria : UnitedKingdom;

    // Whether this is an answer somebody was actually offered. The check a
    // frontend cannot make on its own: a form that does not ask a question is
    // not the same as an API that refuses the answer.
    public static bool IsOffered(CountryCode country, DiversityField field, string? value) =>
        value is not null
        && CategoriesFor(country)
            .FirstOrDefault(category => category.Field == field)?.Options
            .Contains(value, StringComparer.Ordinal) == true;

    public static string? ValueOf(DiversityDeclaration declaration, DiversityField field) =>
        field switch
        {
            DiversityField.Ethnicity => declaration.Ethnicity,
            DiversityField.Disability => declaration.Disability,
            DiversityField.Religion => declaration.Religion,
            DiversityField.VeteranStatus => declaration.VeteranStatus,
            _ => declaration.CaringResponsibilities,
        };
}

public sealed record DiversityTally
{
    public required string Label { get; init; }

    public required int Count { get; init; }

    // Of those who answered, not of the headcount.
    public required int Percentage { get; init; }
}

public sealed record SuppressedBreakdown
{
    public required IReadOnlyList<DiversityTally> Rows { get; init; }

    // How many people fell into groups too small to report.
    public required int SuppressedCount { get; init; }

    public required int SuppressedGroups { get; init; }

    // Answered at all — the denominator for every percentage.
    public required int Declared { get; init; }

    // The headcount who could have answered, so the declaration rate is
    // readable alongside the figures.
    public required int Eligible { get; init; }

    // Nothing could be reported. Distinct from having no data, and said
    // explicitly so a near-empty chart is not read as a gap in collection.
    public bool FullySuppressed => Rows.Count == 0 && SuppressedCount > 0;
}

public static class Suppression
{
    // Below this many people, a group is not reported. Five is the ONS
    // threshold and the one most HR analytics guidance uses: with fewer, a
    // reader who knows the team can usually work out who is who.
    public const int Threshold = 5;

    public static SuppressedBreakdown Tally(
        IReadOnlyCollection<string?> values,
        int eligible,
        int threshold = Threshold)
    {
        List<string> declared = [.. values.OfType<string>()];

        List<DiversityTally> rows = [];
        int suppressedCount = 0;
        int suppressedGroups = 0;

        foreach (IGrouping<string, string> group in declared
            .GroupBy(value => value, StringComparer.Ordinal))
        {
            int count = group.Count();

            if (count < threshold)
            {
                suppressedCount += count;
                suppressedGroups++;
                continue;
            }

            rows.Add(new DiversityTally
            {
                Label = group.Key,
                Count = count,
                Percentage = declared.Count == 0
                    ? 0
                    : (int)Math.Round(
                        count / (decimal)declared.Count * 100m, MidpointRounding.AwayFromZero),
            });
        }

        return new SuppressedBreakdown
        {
            Rows =
            [
                .. rows
                    .OrderByDescending(row => row.Count)
                    .ThenBy(row => row.Label, StringComparer.Ordinal),
            ],
            SuppressedCount = suppressedCount,
            SuppressedGroups = suppressedGroups,
            Declared = declared.Count,
            Eligible = eligible,
        };
    }

    // The share of the workforce that has answered at all. Published with the
    // figures because a breakdown built from a fifth of the staff is a
    // different claim from one built from all of them.
    public static int DeclarationRate(int declared, int eligible) => eligible == 0
        ? 0
        : (int)Math.Round(declared / (decimal)eligible * 100m, MidpointRounding.AwayFromZero);
}
