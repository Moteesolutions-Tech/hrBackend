using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Motee.Tests.Integration;

namespace Motee.Tests.Api;

// Every route requires a token unless it is named here as deliberately not needing one.
//
// This application sets no fallback authorization policy, so an endpoint is protected
// only by the attribute written on it. That makes the absence of an attribute silent:
// nothing fails, nothing warns, and the endpoint simply answers anybody. Four
// self-service routes and three approval routes had drifted open this way, including
// the one that approves and rejects. None of them leaked, because the tenant query
// filter matches nothing for a caller with no tenant — but that is a second line
// holding on its own, and whoever writes the next endpoint will not know it is there.
//
// Enumerated from the real routing table rather than by reading attributes, because
// what protects a route is the metadata the framework ends up with.
[Collection(PostgresCollection.Name)]
public class EndpointAuthenticationTests(PostgresFixture fixture)
{
    // Open on purpose, each for a reason that does not generalise.
    private static readonly HashSet<string> Public = new(StringComparer.Ordinal)
    {
        // Load balancer probes, which have no credential to present.
        "/health",
        "/health/ready",

        // The API description. Mapped only outside production.
        "/openapi/{documentName}.json",
    };

    [SkippableFact]
    public void NoRouteIsReachableWithoutATokenByAccident()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = new(fixture.ConnectionString!);

        // Builds the host, which is what populates the routing table.
        _ = api.CreateClient();

        List<string> open =
        [
            .. api.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .Where(endpoint =>
                    endpoint.Metadata.GetMetadata<IAuthorizeData>() is null

                    // AllowAnonymous is a decision somebody wrote down — registration,
                    // sign-in and the join portal all carry it, and the joiner's token
                    // is their credential. Silence is what this is looking for.
                    && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
                .Select(endpoint => endpoint.RoutePattern.RawText!)
                .Where(route => !Public.Contains(route))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            open.Count == 0,
            "These routes require neither a token nor an explicit AllowAnonymous. Add "
            + "[Authorize], [RequiresPermission], [RequiresEmployee] or [AllowAnonymous], "
            + $"whichever is true of them:\n{string.Join("\n", open)}");
    }
}
