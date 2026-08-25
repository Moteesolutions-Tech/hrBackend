namespace Motee.Application.Organisation;

// The grouping above departments — a division, a region, a company within a group.
//
// It exists so an access level can be confined to part of the organisation:
// "this HR Manager covers Corporate Services" cannot be said with departments alone
// without listing all six and remembering to edit the level when a seventh appears.
//
// Until something could create them, DataScopeKind.BusinessUnit reached nobody. The
// filter walks employee → department → business_unit, and with no units in existence
// and no department assigned to one, it matched nothing every time — so choosing
// "assigned business units" produced a level that silently opened nothing.
public interface IBusinessUnitService
{
    Task<IReadOnlyList<BusinessUnitDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<BusinessUnitDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<BusinessUnitResult> CreateAsync(
        BusinessUnitRequest request,
        CancellationToken cancellationToken = default);

    Task<BusinessUnitResult> UpdateAsync(
        Guid id,
        BusinessUnitRequest request,
        CancellationToken cancellationToken = default);

    Task<BusinessUnitOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public enum BusinessUnitOutcome
{
    Succeeded,
    NotFound,
    DuplicateName,

    // Departments still sit under it. Deleting would detach them, and every access level
    // scoped to this unit would quietly stop reaching their employees — a permission
    // change nobody asked for and nothing records. The tenant deactivates instead.
    HasDepartments,
}

public sealed record BusinessUnitRequest
{
    public required string Name { get; init; }

    public string? Code { get; init; }

    public string? Description { get; init; }

    public bool IsActive { get; init; } = true;
}

public sealed record BusinessUnitResult
{
    public required BusinessUnitOutcome Outcome { get; init; }

    public BusinessUnitDto? BusinessUnit { get; init; }

    public bool Succeeded => Outcome == BusinessUnitOutcome.Succeeded;

    public static BusinessUnitResult Failed(BusinessUnitOutcome outcome) => new() { Outcome = outcome };

    public static BusinessUnitResult Ok(BusinessUnitDto unit) =>
        new() { Outcome = BusinessUnitOutcome.Succeeded, BusinessUnit = unit };
}

public sealed record BusinessUnitDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Code { get; init; }

    public string? Description { get; init; }

    public required bool IsActive { get; init; }


    public required int DepartmentCount { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
