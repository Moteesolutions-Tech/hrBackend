using Motee.Domain.Tenants;

namespace Motee.Application.Tenancy;

public interface ITenantSetupService
{
    Task<TenantSetupDto?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<bool> SaveAsync(
        Guid tenantId,
        TenantSetupRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

public sealed record TenantSetupDto
{
    // Fixed at registration and shown read-only: changing country would move the
    // tenant's currency and tax rules after the fact.
    public required string CompanyName { get; init; }

    public required string CountryCode { get; init; }

    public required string Country { get; init; }

    public string? Industry { get; init; }

    public CompanySize? CompanySize { get; init; }

    public string? CompanyEmailDomain { get; init; }

    public string? CompanyPolicies { get; init; }

    public required string ManagerTitle { get; init; }

    public required string DepartmentLabel { get; init; }

    public required StructureType StructureType { get; init; }

    public required IReadOnlyList<string> EnabledModules { get; init; }

    public required bool OnboardingCompleted { get; init; }

    public DateTimeOffset? OnboardingCompletedAt { get; init; }
}
