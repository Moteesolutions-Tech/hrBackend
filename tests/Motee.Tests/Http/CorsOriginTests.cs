using Motee.Api.Http;

namespace Motee.Tests.Http;

// CORS decides which pages a browser will let talk to this API. The matching is the
// whole of that decision, so it is pinned here rather than left to a config file
// nobody re-reads.
public class CorsOriginTests
{
    private static readonly string[] Allowed =
    [
        "https://app.motee.com",
        "http://localhost:3000",
    ];

    [Fact]
    public void AllowsAnOriginOnTheList()
    {
        Assert.True(CorsOrigins.IsAllowed(Allowed, "https://app.motee.com"));
        Assert.True(CorsOrigins.IsAllowed(Allowed, "http://localhost:3000"));
    }

    [Fact]
    public void RefusesAnythingElse()
    {
        Assert.False(CorsOrigins.IsAllowed(Allowed, "https://motee.com"));
        Assert.False(CorsOrigins.IsAllowed(Allowed, "http://localhost:3001"));
    }

    // A forgotten setting should be a blocked request someone notices, not an API
    // open to every page on the internet.
    [Fact]
    public void NothingConfiguredAllowsNothing()
    {
        Assert.False(CorsOrigins.IsAllowed([], "https://app.motee.com"));
    }

    // The scheme is part of the origin. Allowing https must not allow plain http,
    // where a token could be read off the wire.
    [Fact]
    public void TheSchemeMustMatch()
    {
        Assert.False(CorsOrigins.IsAllowed(Allowed, "http://app.motee.com"));
    }

    // The comparison is whole-origin. A prefix or contains match would let a lookalike
    // domain through.
    [Fact]
    public void ALookalikeDomainIsRefused()
    {
        Assert.False(CorsOrigins.IsAllowed(Allowed, "https://app.motee.com.attacker.com"));
        Assert.False(CorsOrigins.IsAllowed(Allowed, "https://notapp.motee.com"));
        Assert.False(CorsOrigins.IsAllowed(Allowed, "https://app.motee.com.evil"));
    }

    // Origins arrive from the browser and are case-insensitive by host.
    [Fact]
    public void CaseDoesNotMatter()
    {
        Assert.True(CorsOrigins.IsAllowed(Allowed, "https://APP.motee.com"));
    }

    // Every preview deployment of a frontend gets its own hostname, so they cannot be
    // listed one by one.
    [Fact]
    public void AWildcardMatchesASubdomain()
    {
        string[] previews = ["https://*.vercel.app"];

        Assert.True(CorsOrigins.IsAllowed(previews, "https://motee-git-main.vercel.app"));
        Assert.True(CorsOrigins.IsAllowed(previews, "https://motee-abc123.vercel.app"));
    }

    // The wildcard stands for a subdomain, not for nothing and not for a different
    // domain that happens to end the same way.
    [Fact]
    public void AWildcardDoesNotMatchTheBareDomainOrALookalike()
    {
        string[] previews = ["https://*.vercel.app"];

        Assert.False(CorsOrigins.IsAllowed(previews, "https://vercel.app"));
        Assert.False(CorsOrigins.IsAllowed(previews, "https://notvercel.app"));
        Assert.False(CorsOrigins.IsAllowed(previews, "http://motee.vercel.app"));
    }

    [Fact]
    public void AnEmptyOriginIsRefused()
    {
        Assert.False(CorsOrigins.IsAllowed(Allowed, ""));
        Assert.False(CorsOrigins.IsAllowed(Allowed, "   "));
    }
}
