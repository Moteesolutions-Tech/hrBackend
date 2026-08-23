using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Motee.Application.Countries;
using Motee.Domain.Common;
using Motee.Domain.Countries;

namespace Motee.Application.Localization;

internal sealed class VisitorLocaleService(
    IEnumerable<IVisitorCountryResolver> resolvers,
    ICountryProfileProvider countryProfiles,
    IConfiguration configuration,
    ILogger<VisitorLocaleService> logger) : IVisitorLocaleService
{
    public VisitorLocaleDto Detect()
    {
        string? detected = null;
        string source = "default";

        foreach (IVisitorCountryResolver resolver in resolvers)
        {
            detected = resolver.Resolve();

            if (!string.IsNullOrWhiteSpace(detected))
            {
                source = resolver.Source;
                break;
            }
        }

        bool isSupported = CountryCode.TryParse(detected, out CountryCode countryCode);

        if (!isSupported)
        {
            countryCode = DefaultCountry();
            source = detected is null ? "default" : $"{source}:unsupported";
        }

        ICountryProfile profile = countryProfiles.Get(countryCode);

        logger.LogInformation(
            "Visitor country {DetectedCountry} (supported: {IsSupported}) resolved to {SelectedCountry} via {DetectionSource}",
            detected ?? "none",
            isSupported,
            profile.CountryCode.Value,
            source);

        return new VisitorLocaleDto
        {
            DetectedCountryCode = detected,
            IsSupported = isSupported,
            CountryCode = profile.CountryCode.Value,
            Country = profile.DisplayName,
            Currency = profile.CurrencyCode,
            CurrencySymbol = profile.CurrencySymbol,
            Locale = profile.Locale,
            Timezone = profile.TimeZoneId,
            Source = source,
        };
    }

    private CountryCode DefaultCountry() =>
        CountryCode.TryParse(configuration["Locale:DefaultCountry"], out CountryCode configured)
            ? configured
            : CountryCode.UnitedKingdom;
}
