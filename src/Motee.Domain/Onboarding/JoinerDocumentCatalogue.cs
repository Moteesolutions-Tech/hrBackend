using Motee.Domain.Common;

namespace Motee.Domain.Onboarding;

public sealed record JoinerDocumentSpec
{
    public required JoinerDocumentKind Kind { get; init; }

    public required string Label { get; init; }

    // What to actually send. Without it, "Proof of Address" gets a payslip from 2019 and
    // somebody has to ask again, which is the round trip the hint exists to avoid.
    public required string Hint { get; init; }

    public required bool Required { get; init; }

    // Null means both countries ask for it.
    public CountryCode? Country { get; init; }
}

// Which upload slots a joiner sees, and which they cannot submit without.
//
// Served rather than hard-coded in the client for the usual reason: the list differs by
// country, and a slot the interface offers that the backend will not accept — or demands
// that it does not — is a form somebody cannot complete.
public static class JoinerDocumentCatalogue
{
    // Only the Nigerian guarantor IDs are required.
    //
    // Passport, Right to Work and Proof of Address are deliberately optional. That is the
    // client's stated instruction, and it is worth recording because it is surprising:
    // right to work is a statutory pre-employment check in the UK, so leaving it optional
    // means the platform does not enforce something an employer is separately obliged to
    // do. Enforcing it here would block joiners whose evidence is a share code handled
    // outside the system, which is the case they asked us not to block.
    public static readonly IReadOnlyList<JoinerDocumentSpec> All =
    [
        new()
        {
            Kind = JoinerDocumentKind.Passport,
            Label = "Passport",
            Hint = "Photo page showing your name, number and expiry",
            Required = false,
        },
        new()
        {
            Kind = JoinerDocumentKind.RightToWork,
            Label = "Right to Work evidence",
            Hint = "Share code, BRP or another accepted document",
            Required = false,
            Country = CountryCode.UnitedKingdom,
        },
        new()
        {
            Kind = JoinerDocumentKind.DrivingLicence,
            Label = "Driving Licence",
            Hint = "Both sides if you have a photocard",
            Required = false,
        },
        new()
        {
            Kind = JoinerDocumentKind.Visa,
            Label = "Visa",
            Hint = "Only if your right to work depends on one",
            Required = false,
            Country = CountryCode.UnitedKingdom,
        },
        new()
        {
            Kind = JoinerDocumentKind.ProofOfAddress,
            Label = "Proof of Address",
            Hint = "Utility bill or bank statement from the last 3 months",
            Required = false,
        },
        new()
        {
            Kind = JoinerDocumentKind.Qualifications,
            Label = "Qualifications",
            Hint = "Optional — certificates relevant to your role",
            Required = false,
        },
        new()
        {
            Kind = JoinerDocumentKind.Guarantor1Id,
            Label = "Guarantor 1 — ID Document",
            Hint = "National ID, passport or driver's licence",
            Required = true,
            Country = CountryCode.Nigeria,
        },
        new()
        {
            Kind = JoinerDocumentKind.Guarantor2Id,
            Label = "Guarantor 2 — ID Document",
            Hint = "National ID, passport or driver's licence",
            Required = true,
            Country = CountryCode.Nigeria,
        },
    ];

    public static IReadOnlyList<JoinerDocumentSpec> For(CountryCode country) =>
        [.. All.Where(spec => spec.Country is null || spec.Country == country)];

    // A slot offered to one country must not be accepted from the other. Without this a
    // UK joiner could file a guarantor ID that nobody will ever look at, and a Nigerian
    // one a visa that means nothing there.
    public static bool Accepts(CountryCode country, JoinerDocumentKind kind) =>
        For(country).Any(spec => spec.Kind == kind);

    // What is still outstanding. Returns the specs rather than a boolean so a refusal can
    // name them — "you still need Guarantor 2's ID" rather than "incomplete".
    public static IReadOnlyList<JoinerDocumentSpec> Missing(
        CountryCode country,
        IEnumerable<JoinerDocumentKind> supplied)
    {
        HashSet<JoinerDocumentKind> have = [.. supplied];

        return [.. For(country).Where(spec => spec.Required && !have.Contains(spec.Kind))];
    }
}
