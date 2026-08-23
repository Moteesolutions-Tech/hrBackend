namespace Motee.Application.Tenancy;

// What the setup wizard submits. Company name and country are absent on purpose:
// both are fixed at registration, and country decides currency and tax.
public sealed record TenantSetupRequest
{
    public required string Industry { get; init; }

    public required string CompanySize { get; init; }

    public string? CompanyEmailDomain { get; init; }

    public string? CompanyPolicies { get; init; }

    public required string ManagerTitle { get; init; }

    public required string DepartmentLabel { get; init; }

    public required string StructureType { get; init; }

    public required IReadOnlyList<string> EnabledModules { get; init; }
}
