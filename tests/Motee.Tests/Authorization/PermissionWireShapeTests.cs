using System.Text.Json;
using System.Text.Json.Serialization;
using Motee.Domain.Authorization;
using Motee.Domain.Identity;

namespace Motee.Tests.Authorization;

// GET /auth/me returns the domain matrix directly rather than a duplicate DTO, so
// this pins the wire shape the frontend's useCan hook reads. A rename or reordering
// in the domain would otherwise change the API silently.
public class PermissionWireShapeTests
{
    // Mirrors the converter registered on AddControllers.
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static JsonElement Serialise(ModulePermission permission) =>
        JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(permission, Api));

    private static ModulePermission Employees(Role role) =>
        AccessLevels.For(role).Modules.First(m => m.Module == "organization.employees");

    // Three fields, not four. Scope moved to the access level, because one answer per
    // level is what lets scopes intersect when someone holds several.
    [Fact]
    public void APermissionCarriesTheModuleAccessAndActions()
    {
        JsonElement json = Serialise(Employees(Role.HrAdmin));

        Assert.Equal(
            ["module", "access", "actions"],
            json.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void ActionsAreLowercaseStringsNotOrdinals()
    {
        JsonElement json = Serialise(Employees(Role.HrAdmin));

        // Order follows the enum, and the set is whatever the template grants after
        // dependencies are expanded — so the assertion is on form, not on a list that
        // changes whenever a default is retuned.
        string[] actions =
            [.. json.GetProperty("actions").EnumerateArray().Select(action => action.GetString()!)];

        Assert.Contains("view", actions);
        Assert.Contains("delete", actions);
        Assert.DoesNotContain(actions, action => int.TryParse(action, out _));
    }

    // Scope is now on the level. It serialises as an object rather than a bare
    // string, because a named kind carries the departments or business units it
    // covers alongside the kind itself.
    [Theory]
    [InlineData(Role.HrAdmin, "all")]
    [InlineData(Role.LineManager, "directReports")]
    public void TheLevelsScopeIsACamelCaseKind(Role role, string expected)
    {
        JsonElement scope = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(AccessLevels.For(role).Scope, Api));

        Assert.Equal(expected, scope.GetProperty("kind").GetString());
        Assert.True(scope.TryGetProperty("departmentIds", out _));
    }

    [Fact]
    public void ADeniedModuleReportsNoActions()
    {
        ModulePermission settings = AccessLevels.For(Role.LineManager).Modules
            .First(module => module.Module == "admin.settings");

        JsonElement json = Serialise(settings);

        Assert.False(json.GetProperty("access").GetBoolean());
        Assert.Empty(json.GetProperty("actions").EnumerateArray());
    }

    [Fact]
    public void EveryModuleInTheCatalogueIsReturned()
    {
        Assert.Equal(
            ModuleCatalogue.All.Count,
            AccessLevels.For(Role.HrAdmin).Modules.Count);
    }
}
