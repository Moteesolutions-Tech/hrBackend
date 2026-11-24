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

    public CompanyIdentifier Registration => new()
    {
        Label = "Company Registration",
        NumberLabel = "RC Number",
        Authority = "Corporate Affairs Commission",
        Description =
            "Your certificate of incorporation from the Corporate Affairs Commission, "
            + "carrying the RC number.",
    };

    public CompanyIdentifier TaxIdentifier => new()
    {
        Label = "Tax Identification",
        NumberLabel = "TIN",
        Authority = "Federal Inland Revenue Service",
        Description =
            "Your Taxpayer Identification Number from the Federal Inland Revenue Service.",
    };
}
