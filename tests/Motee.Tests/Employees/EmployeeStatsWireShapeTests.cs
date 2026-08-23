using System.Text.Json;
using System.Text.Json.Serialization;
using Motee.Application.Employees;
using Motee.Domain.Employees;

namespace Motee.Tests.Employees;

// The stat cards and the tab strip read this directly, so the shape is pinned here.
// The counts themselves are covered against a real database in the integration
// tests; this is only about what goes over the wire.
public class EmployeeStatsWireShapeTests
{
    // Mirrors the converter registered on AddControllers.
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static JsonElement Serialise() => JsonSerializer.Deserialize<JsonElement>(
        JsonSerializer.Serialize(
            new EmployeeStatsDto
            {
                Headcount = 12,
                ByStatus =
                [
                    .. Enum.GetValues<EmployeeStatus>().Select(status => new EmployeeStatusCount
                    {
                        Status = status,
                        Count = 0,
                    }),
                ],
            },
            Api));

    [Fact]
    public void CarriesHeadcountAndTheStatusBreakdown()
    {
        Assert.Equal(
            ["headcount", "byStatus"],
            Serialise().EnumerateObject().Select(property => property.Name));
    }

    // A tab keyed on "on_leave" or on the ordinal 4 would break the moment the enum
    // is reordered.
    [Fact]
    public void StatusesAreCamelCaseStringsNotOrdinals()
    {
        string[] statuses = [.. Serialise()
            .GetProperty("byStatus")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("status").GetString()!)];

        Assert.Contains("onLeave", statuses);
        Assert.Contains("probation", statuses);
        Assert.DoesNotContain(statuses, status => int.TryParse(status, out _));
    }

    // Zero-count statuses are present, or a tab would vanish rather than show 0.
    [Fact]
    public void EveryStatusIsPresentInLifecycleOrder()
    {
        Assert.Equal(
            Enum.GetValues<EmployeeStatus>().Select(status => status.ToString()),
            Serialise()
                .GetProperty("byStatus")
                .EnumerateArray()
                .Select(entry => entry.GetProperty("status").GetString()!),
            StringComparer.OrdinalIgnoreCase);
    }
}
