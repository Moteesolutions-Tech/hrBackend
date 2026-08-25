using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Integration;

namespace Motee.Tests.Api;

// Registration must answer identically whether or not the address already has an
// account. Anything that differs - status, body, wording - lets an anonymous caller
// test a list of addresses and learn which belong to Motee customers, which for an HR
// product means learning who the customers are.
//
// These compare the two responses directly rather than asserting a shape, so any field
// added later that is knowable in only one case fails here.
[Collection(PostgresCollection.Name)]
public class RegistrationDisclosureTests(PostgresFixture fixture)
{
    private const string Email = "ada@acme.com";

    private static object Body(string companyName) => new
    {
        firstName = "Ada",
        lastName = "Okafor",
        email = Email,
        companyName,
        password = "correct-horse",
        countryCode = "NG",
    };

    private static async Task<(HttpStatusCode Status, string Body)> RegisterAsync(
        ApiFactory api,
        string companyName)
    {
        HttpResponseMessage response = await api.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/register", Body(companyName));

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task TheSecondRegistrationIsIndistinguishableFromTheFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        using ApiFactory api = new(fixture.ConnectionString!);

        (HttpStatusCode firstStatus, string firstBody) = await RegisterAsync(api, "Acme Corporation");
        (HttpStatusCode secondStatus, string secondBody) = await RegisterAsync(api, "Globex Industries");

        Assert.Equal(HttpStatusCode.Created, firstStatus);
        Assert.Equal(firstStatus, secondStatus);

        // Byte for byte. The response carries only the email that was submitted, so
        // there is nothing legitimate left to differ.
        Assert.Equal(firstBody, secondBody);
    }

    // The response must not carry identifiers - they exist only when something was
    // created, so including them announces which case the caller hit.
    [SkippableFact]
    public async Task NoIdentifiersAreReturned()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        using ApiFactory api = new(fixture.ConnectionString!);

        (_, string body) = await RegisterAsync(api, "Acme Corporation");

        JsonElement data = JsonDocument.Parse(body).RootElement.GetProperty("data");

        Assert.False(data.TryGetProperty("userId", out _));
        Assert.False(data.TryGetProperty("tenantId", out _));
        Assert.False(data.TryGetProperty("tenantSlug", out _));
    }

    // The point of the whole exercise: the duplicate attempt must not quietly create a
    // second company alongside the first.
    [SkippableFact]
    public async Task TheDuplicateAttemptCreatesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        using ApiFactory api = new(fixture.ConnectionString!);

        await RegisterAsync(api, "Acme Corporation");
        await RegisterAsync(api, "Globex Industries");

        await using MoteeDbContext context = fixture.CreateContext();

        Assert.Single(await context.Tenants.IgnoreQueryFilters().ToListAsync());
        Assert.Single(await context.Users.IgnoreQueryFilters().ToListAsync());
    }

    // verify-otp used to take a user id, which registration no longer returns. An
    // address with no account must answer exactly as a wrong code does, or it becomes
    // the oracle that register stopped being.
    [SkippableFact]
    public async Task VerifyingAnUnknownAddressLooksLikeAWrongCode()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        using ApiFactory api = new(fixture.ConnectionString!);

        await RegisterAsync(api, "Acme Corporation");

        HttpResponseMessage known = await api.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/verify-otp", new { email = Email, code = "000000" });

        HttpResponseMessage unknown = await api.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/verify-otp", new { email = "nobody@nowhere.com", code = "000000" });

        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(
            await known.Content.ReadAsStringAsync(),
            await unknown.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task ResendingToAnUnknownAddressLooksLikeSuccess()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        using ApiFactory api = new(fixture.ConnectionString!);

        HttpResponseMessage unknown = await api.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/resend-otp", new { email = "nobody@nowhere.com" });

        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
    }
}
