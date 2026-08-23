using Microsoft.Extensions.Configuration;

namespace Motee.Infrastructure.Common;

// Links that go into emails point at the web app, not the API. A relative path is
// not clickable in a mail client, so the base has to come from configuration —
// deriving it from the request would let a forged Host header rewrite where an
// invitation sends people.
internal sealed class AppLinks(IConfiguration configuration)
{
    private string BaseUrl =>
        configuration["App:BaseUrl"]?.TrimEnd('/')
        ?? throw new InvalidOperationException(
            "App:BaseUrl is not configured. Emailed links need the address of the web "
            + "app, for example https://app.motee.com.");

    public string Join(string token) => $"{BaseUrl}/join/{token}";
}
