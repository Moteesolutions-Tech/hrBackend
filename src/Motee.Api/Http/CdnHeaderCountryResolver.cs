using System.Security.Cryptography;
using System.Text;
using Motee.Application.Localization;

namespace Motee.Api.Http;

// CDNs resolve the viewer's country at the edge and pass it down as a header, so
// there is no lookup cost and no third party sees the visitor's IP. Nothing is
// present in local development or behind a bare load balancer — the caller then
// falls back to the configured default.
internal sealed class CdnHeaderCountryResolver(
    IHttpContextAccessor httpContextAccessor,
    IConfiguration configuration) : IVisitorCountryResolver
{
    private const string OriginVerifyHeader = "X-Origin-Verify";

    private static readonly string[] HeaderNames =
    [
        "CloudFront-Viewer-Country",
        "CF-IPCountry",
        "X-Vercel-IP-Country",
        "X-Geo-Country",
    ];

    public string Source => "cdn-header";

    public string? Resolve()
    {
        HttpContext? context = httpContextAccessor.HttpContext;

        if (context is null || !IsFromTrustedEdge(context))
        {
            return null;
        }

        foreach (string headerName in HeaderNames)
        {
            string? value = context.Request.Headers[headerName].FirstOrDefault();

            // Cloudflare sends "XX" for anonymised or unknown viewers.
            if (!string.IsNullOrWhiteSpace(value)
                && value.Length == 2
                && !value.Equals("XX", StringComparison.OrdinalIgnoreCase))
            {
                return value.ToUpperInvariant();
            }
        }

        return null;
    }

    // Geo headers are only meaningful if the CDN set them. Anyone who can reach the
    // origin directly can forge them, so once a secret is configured the request must
    // carry it — CloudFront adds it as a custom origin header, and callers bypassing
    // the distribution cannot. Unset (local dev) means no check.
    private bool IsFromTrustedEdge(HttpContext context)
    {
        string? expected = configuration["Cdn:OriginVerifySecret"];

        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        string? presented = context.Request.Headers[OriginVerifyHeader].FirstOrDefault();

        return !string.IsNullOrEmpty(presented)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(presented),
                Encoding.UTF8.GetBytes(expected));
    }
}
