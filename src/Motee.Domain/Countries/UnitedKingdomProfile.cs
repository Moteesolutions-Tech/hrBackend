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

    public CompanyIdentifier Registration => new()
    {
        Label = "Company Registration",
        NumberLabel = "Company Number",
        Authority = "Companies House",
        Description =
            "Your certificate of incorporation from Companies House, carrying the "
            + "eight-character company number.",
    };

    // VAT rather than a general tax reference, because VAT registration is the
    // one a company holds a certificate for and can be asked to prove. Not every
    // company is registered — below the threshold there is nothing to show — so
    // this requirement can legitimately be left alone.
    public CompanyIdentifier TaxIdentifier => new()
    {
        Label = "VAT Registration",
        NumberLabel = "VAT Number",
        Authority = "HM Revenue & Customs",
        Description =
            "Your VAT registration certificate from HMRC. Companies below the "
            + "registration threshold have none.",
    };
}
