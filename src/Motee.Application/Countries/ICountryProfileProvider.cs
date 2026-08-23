using Motee.Domain.Common;
using Motee.Domain.Countries;

namespace Motee.Application.Countries;

public interface ICountryProfileProvider
{
    ICountryProfile Get(CountryCode countryCode);

    IReadOnlyCollection<ICountryProfile> All { get; }
}
