using Motee.Domain.Common;
using Motee.Domain.Countries;

namespace Motee.Application.Countries;

internal sealed class CountryProfileProvider : ICountryProfileProvider
{
    private readonly Dictionary<CountryCode, ICountryProfile> _profiles;

    public CountryProfileProvider(IEnumerable<ICountryProfile> profiles)
    {
        _profiles = profiles.ToDictionary(profile => profile.CountryCode);
    }

    public IReadOnlyCollection<ICountryProfile> All => _profiles.Values;

    public ICountryProfile Get(CountryCode countryCode)
    {
        if (!_profiles.TryGetValue(countryCode, out ICountryProfile? profile))
        {
            throw new InvalidOperationException($"No country profile registered for '{countryCode}'.");
        }

        return profile;
    }
}
