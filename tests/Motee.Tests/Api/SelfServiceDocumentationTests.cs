using System.Net.Http.Json;
using System.Text.Json;
using Motee.Tests.Integration;

namespace Motee.Tests.Api;

// What the published contract says about being turned away from self-service.
//
// The refusal is only useful to the frontend if it is documented: a client that does
// not know 1006 exists cannot hide the screens it is telling them to hide, and will
// render the owner an error instead. The generic 403 the transformer adds to every
// authorised endpoint would have covered this one too and said the wrong thing —
// "this access level does not grant it" — so the ordering that puts the specific one
// first is load bearing, and this is what holds it in place.
[Collection(PostgresCollection.Name)]
public class SelfServiceDocumentationTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task TheOwnersRefusalIsInTheContract()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = new(fixture.ConnectionString!);

        JsonElement document = await api.CreateClient()
            .GetFromJsonAsync<JsonElement>("/openapi/v1.json");

        JsonElement paths = document.GetProperty("paths");

        Assert.True(
            paths.TryGetProperty("/api/v1/leave/requests/mine", out JsonElement mine),
            string.Join(", ", paths.EnumerateObject()
                .Select(path => path.Name)
                .Where(name => name.Contains("leave", StringComparison.Ordinal))));

        JsonElement responses = mine.GetProperty("get").GetProperty("responses");

        Assert.True(
            responses.TryGetProperty("403", out JsonElement forbidden),
            string.Join(", ", responses.EnumerateObject().Select(status => status.Name)));

        string example = forbidden
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("example")
            .GetProperty("responseCode")
            .GetString()!;

        Assert.Equal("1006", example);

        // The description has to say it is about the account rather than its rights,
        // or a frontend developer reads "403" and builds a permission check.
        Assert.Contains(
            "manages the company",
            forbidden.GetProperty("description").GetString()!,
            StringComparison.Ordinal);
    }

    // The generic one still applies everywhere else. Replacing it rather than adding
    // to it would leave every permission-gated endpoint claiming the caller is an
    // owner, which is wrong almost everywhere.
    [SkippableFact]
    public async Task EverywhereElseKeepsTheOrdinaryForbidden()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = new(fixture.ConnectionString!);

        JsonElement document = await api.CreateClient()
            .GetFromJsonAsync<JsonElement>("/openapi/v1.json");

        string example = document
            .GetProperty("paths")
            .GetProperty("/api/v1/employees")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("403")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("example")
            .GetProperty("responseCode")
            .GetString()!;

        Assert.Equal("403", example);
    }
}
