using Motee.Domain.Common;

namespace Motee.Domain.Countries;

public sealed class NigeriaProfile : ICountryProfile
{
    public CountryCode CountryCode => CountryCode.Nigeria;

    public string DisplayName => "Nigeria";

    public string CurrencyCode => "NGN";

    public string CurrencySymbol => "₦";

    public string Locale => "en-NG";

    public string TimeZoneId => "Africa/Lagos";

    public bool UsesPayeStarterRecords => false;
}
