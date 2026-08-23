using Motee.Domain.Common;

namespace Motee.Domain.Countries;

public sealed class UnitedKingdomProfile : ICountryProfile
{
    public CountryCode CountryCode => CountryCode.UnitedKingdom;

    public string DisplayName => "United Kingdom";

    public string CurrencyCode => "GBP";

    public string CurrencySymbol => "£";

    public string Locale => "en-GB";

    public string TimeZoneId => "Europe/London";

    public bool UsesPayeStarterRecords => true;
}
