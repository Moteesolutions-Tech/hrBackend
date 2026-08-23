using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Application.Countries;
using Motee.Application.Localization;
using Motee.Domain.Countries;

namespace Motee.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/locale")]
public class LocaleController(
    ITenantLocaleService localeService,
    IVisitorLocaleService visitorLocaleService,
    ICountryProfileProvider countryProfiles) : ControllerBase
{
    // Pre-selects the country toggle on the login / sign-up cards. A hint only —
    // the visitor can switch, and once they authenticate GET /api/locale (driven by
    // the tenant record) is what governs currency and tax.
    [HttpGet("detect")]
    [AllowAnonymous]
    public ActionResult<VisitorLocaleDto> Detect() => Ok(visitorLocaleService.Detect());

    // Replaces the frontend's static import of nigeria.json / uk.json. The country
    // comes from the tenant record, so an admin travelling abroad still sees their
    // own jurisdiction.
    [HttpGet]
    [Authorize]
    public async Task<ActionResult<TenantLocaleDto>> GetCurrent(CancellationToken cancellationToken)
    {
        TenantLocaleDto? locale = await localeService.GetCurrentAsync(cancellationToken);

        return locale is null ? NotFound() : Ok(locale);
    }

    // Supported jurisdictions, for the tenant-onboarding country picker.
    [HttpGet("countries")]
    [AllowAnonymous]
    public ActionResult<IEnumerable<object>> GetSupportedCountries()
    {
        IEnumerable<object> countries = countryProfiles.All
            .OrderBy(profile => profile.DisplayName)
            .Select(profile => new
            {
                countryCode = profile.CountryCode.Value,
                name = profile.DisplayName,
                currency = profile.CurrencyCode,
                currencySymbol = profile.CurrencySymbol,
                locale = profile.Locale,
                timezone = profile.TimeZoneId,
            });

        return Ok(countries);
    }
}
