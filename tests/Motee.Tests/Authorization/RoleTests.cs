using Motee.Domain.Authorization;
using Motee.Domain.Identity;

namespace Motee.Tests.Authorization;

public class RoleTests
{
    [Fact]
    public void EveryRoleHasASlug()
    {
        Assert.All(Enum.GetValues<Role>(), role =>
            Assert.False(string.IsNullOrWhiteSpace(Roles.ToSlug(role))));
    }

    // One convention: the member name is the stored value, with no transformation
    // between the enum, roles.name, the JWT claim and the seeded id.
    [Theory]
    [InlineData(Role.SuperAdmin, "superAdmin")]
    [InlineData(Role.HrAdmin, "hrAdmin")]
    [InlineData(Role.HrManager, "hrManager")]
    [InlineData(Role.LineManager, "lineManager")]
    [InlineData(Role.ItAdmin, "itAdmin")]
    [InlineData(Role.ReadOnly, "readOnly")]
    // camelCase, the same form every enum takes on the wire. These are template ids
    // now — what a seeded access level records it came from — not role names Identity
    // stores, because Identity's roles are gone.
    public void SlugsMatchWhatIsStored(Role role, string expected)
    {
        Assert.Equal(expected, Roles.ToSlug(role));
    }

    [Fact]
    public void SlugsAreUnique()
    {
        string[] slugs = [.. Enum.GetValues<Role>().Select(Roles.ToSlug)];

        Assert.Equal(slugs.Length, slugs.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EverySlugParsesBackToItsRole()
    {
        Assert.All(Enum.GetValues<Role>(), role =>
        {
            Assert.True(Roles.TryParse(Roles.ToSlug(role), out Role parsed));
            Assert.Equal(role, parsed);
        });
    }

    [Theory]
    [InlineData("hradmin")]
    [InlineData("HRADMIN")]
    [InlineData("  HrAdmin  ")]
    public void ParsingToleratesCasingAndSpace(string slug)
    {
        Assert.True(Roles.TryParse(slug, out Role parsed));
        Assert.Equal(Role.HrAdmin, parsed);
    }

    // The claim is untrusted input, so anything unrecognised must simply fail.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("ADMIN")]
    [InlineData("HR-ADMIN")]
    [InlineData("Hr Admin")]
    [InlineData("0")]
    [InlineData("1")]
    public void ParsingRefusesAnythingElse(string? slug)
    {
        Assert.False(Roles.TryParse(slug, out _));
    }

    [Fact]
    public void EveryRoleHasItsOwnActionSet()
    {
        Assert.All(Enum.GetValues<Role>(), role =>
            Assert.NotEmpty(DefaultAccessLevels.ActionsFor(role)));
    }

    // An unrecognised claim yields an empty matrix rather than throwing, so a token
    // carrying a role we removed locks the user out instead of erroring.
    [Fact]
    public void AnUnknownRoleClaimResolvesToNoPermissions()
    {
        AccessLevelPermissions level = AccessLevels.ForTemplate("NOT-A-ROLE");

        Assert.All(level.Modules, module =>
        {
            Assert.False(module.Access);
            Assert.Empty(module.Actions);
        });
    }

    [Fact]
    public void ANullRoleClaimResolvesToNoPermissions()
    {
        Assert.All(AccessLevels.ForTemplate(null).Modules, module => Assert.False(module.Access));
    }

}
