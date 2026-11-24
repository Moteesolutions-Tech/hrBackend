using Motee.Domain.Common;

namespace Motee.Domain.Countries;

public interface ICountryProfile
{
    CountryCode CountryCode { get; }

    string DisplayName { get; }

    string CurrencyCode { get; }

    string CurrencySymbol { get; }

    string Locale { get; }

    string TimeZoneId { get; }

    // UK PAYE new-starter capture (P45 / Starter Checklist). Nigerian tenants use
    // TIN + state IRS instead and never generate these records.
    bool UsesPayeStarterRecords { get; }

    // What a company's registration and tax numbers are called here, and who
    // issues them. Nigeria registers with the CAC and holds a TIN; the UK
    // registers with Companies House and holds a VAT number — and a screen
    // asking a Nigerian company for its "Company Number" is asking for
    // something that does not exist.
    //
    // Here rather than in a locale bundle the client assembles, because the
    // labels and the rules that go with them have to agree: the thing the form
    // asks for and the thing the verification checks are the same thing.
    CompanyIdentifier Registration { get; }

    CompanyIdentifier TaxIdentifier { get; }
}

public sealed record CompanyIdentifier
{
    // What the whole thing is called — "Company Registration", "Tax ID".
    public required string Label { get; init; }

    // What the number itself is called — "RC Number", "Company Number".
    public required string NumberLabel { get; init; }

    // Who issues it. What makes a verification mean anything.
    public required string Authority { get; init; }

    public required string Description { get; init; }
}
