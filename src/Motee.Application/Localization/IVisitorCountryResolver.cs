namespace Motee.Application.Localization;

public interface IVisitorCountryResolver
{
    string Source { get; }

    // ISO alpha-2, or null when this resolver cannot tell. May return a country
    // Motee does not operate in yet (e.g. "GH") — the caller decides what to do.
    string? Resolve();
}
