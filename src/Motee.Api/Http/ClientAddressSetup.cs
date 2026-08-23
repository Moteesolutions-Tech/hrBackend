using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Motee.Api.Http;

internal static class ClientAddressSetup
{
    // X-Forwarded-For is client-supplied and trivially forged. ASP.NET Core therefore
    // trusts only loopback by default. Listing your load balancer / VPC range in
    // Network:TrustedProxies is what makes the header authoritative; clearing the
    // known-proxy lists without configuring them would let any caller spoof the IP
    // recorded in the audit trail.
    public static IServiceCollection AddClientAddressResolution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string[] trustedProxies = configuration
            .GetSection("Network:TrustedProxies")
            .Get<string[]>() ?? [];

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = configuration.GetValue<int?>("Network:ForwardLimit") ?? 1;

            if (trustedProxies.Length == 0)
            {
                return;
            }

            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();

            foreach (string entry in trustedProxies)
            {
                if (entry.Contains('/', StringComparison.Ordinal))
                {
                    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(entry));
                }
                else
                {
                    options.KnownProxies.Add(IPAddress.Parse(entry));
                }
            }
        });

        return services;
    }
}
