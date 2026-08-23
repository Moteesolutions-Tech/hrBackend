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
}
