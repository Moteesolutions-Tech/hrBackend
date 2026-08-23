using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Domain.Auth;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class RefreshTokenServiceTests(PostgresFixture fixture)
{
    private static RegisterTenantRequest Request() => new()
    {
        FirstName = "Ada",
        LastName = "Okafor",
        Email = "ada@acme.com",
        CompanyName = "Acme Corporation",
        Password = "correct-horse",
        CountryCode = "NG",
    };

    private async Task<(Guid UserId, ServiceProvider Provider)> ArrangeAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(Request());
        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        return (registration.UserId, fixture.BuildProvider());
    }

    private static IRefreshTokenService Service(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IRefreshTokenService>();

    [SkippableFact]
    public async Task IssuesATokenThatRotatesIntoAFreshPair()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        IssuedRefreshToken issued = await Service(owned).IssueAsync(userId, "127.0.0.1");
        RefreshResult result = await Service(owned).RotateAsync(issued.Value, "127.0.0.1");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.AccessToken);
        Assert.NotNull(result.RefreshToken);
        Assert.NotEqual(issued.Value, result.RefreshToken.Value);
    }

    // The raw value must never be recoverable from the database.
    [SkippableFact]
    public async Task StoresOnlyAHashOfTheToken()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        IssuedRefreshToken issued = await Service(owned).IssueAsync(userId, null);

        await using MoteeDbContext context = fixture.CreateContext();
        RefreshToken stored = await context.RefreshTokens.SingleAsync();

        Assert.NotEqual(issued.Value, stored.TokenHash);
        Assert.Equal(64, stored.TokenHash.Length);
        Assert.DoesNotContain(issued.Value, stored.TokenHash, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task TheOldTokenStopsWorkingOnceRotated()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        IssuedRefreshToken first = await Service(owned).IssueAsync(userId, null);
        RefreshResult rotated = await Service(owned).RotateAsync(first.Value, null);

        Assert.True(rotated.Succeeded);

        RefreshResult replay = await Service(owned).RotateAsync(first.Value, null);

        Assert.False(replay.Succeeded);
    }

    // The reason rotation exists. Someone copies a token, the real client rotates
    // first, then the copy is presented — that is a leak, and every session goes.
    [SkippableFact]
    public async Task ReplayingARotatedTokenIsTreatedAsALeakAndKillsEverySession()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        IssuedRefreshToken stolen = await Service(owned).IssueAsync(userId, null);
        RefreshResult legitimate = await Service(owned).RotateAsync(stolen.Value, null);
        Assert.True(legitimate.Succeeded);

        RefreshResult attacker = await Service(owned).RotateAsync(stolen.Value, null);
        Assert.Equal(RefreshOutcome.Reused, attacker.Outcome);

        // The token the legitimate client is holding must die too — the leak means
        // we cannot tell which party is genuine.
        RefreshResult afterBreach = await Service(owned).RotateAsync(legitimate.RefreshToken!.Value, null);
        Assert.False(afterBreach.Succeeded);

        await using MoteeDbContext context = fixture.CreateContext();
        Assert.Empty(await context.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ToListAsync());
    }

    [SkippableFact]
    public async Task AnUnknownTokenIsRefusedWithoutRevealingThatItIsUnknown()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        RefreshResult result = await Service(owned).RotateAsync("not-a-real-token", null);

        Assert.Equal(RefreshOutcome.Revoked, result.Outcome);
    }

    [SkippableFact]
    public async Task RotationRecordsTheSuccessorSoTheChainIsTraceable()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        IssuedRefreshToken first = await Service(owned).IssueAsync(userId, null);
        await Service(owned).RotateAsync(first.Value, null);

        await using MoteeDbContext context = fixture.CreateContext();
        List<RefreshToken> tokens = await context.RefreshTokens
            .OrderBy(token => token.CreatedAt)
            .ToListAsync();

        Assert.Equal(2, tokens.Count);
        Assert.NotNull(tokens[0].RevokedAt);
        Assert.Equal(tokens[1].Id, tokens[0].ReplacedByTokenId);
        Assert.Null(tokens[1].RevokedAt);
    }

    [SkippableFact]
    public async Task SigningOutRevokesEverythingWithoutLookingLikeALeak()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        IssuedRefreshToken issued = await Service(owned).IssueAsync(userId, null);
        await Service(owned).RevokeAllAsync(userId);

        RefreshResult result = await Service(owned).RotateAsync(issued.Value, null);

        Assert.Equal(RefreshOutcome.Revoked, result.Outcome);
    }

    [SkippableFact]
    public async Task EachIssuedTokenIsDistinct()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        IssuedRefreshToken first = await Service(owned).IssueAsync(userId, null);
        IssuedRefreshToken second = await Service(owned).IssueAsync(userId, null);

        Assert.NotEqual(first.Value, second.Value);
    }
}
