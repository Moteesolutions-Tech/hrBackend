using Motee.Domain.Common;

namespace Motee.Domain.Organisation;

public enum DepartmentStatus
{
    Active,
    Inactive,
    Restructuring,
}

public class Department : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    // Short uppercase identifier the tenant recognises, e.g. ENG.
    public required string Code { get; set; }

    public string? Description { get; set; }

    // The create modal takes a typed-in name. A reference is stored instead:
    // approvals resolve DEPARTMENT_HEAD to a person, and a name can neither be an
    // approver nor survive that employee being renamed.
    public Guid? HeadEmployeeId { get; set; }

    // Which business unit this sits under, when the tenant groups departments that
    // way. Null is normal — a company with a flat structure has no business units at
    // all, and an access level scoped to one simply reaches nobody there.
    //
    // This is the link that makes business-unit scoping mean something: an employee
    // belongs to a department, and the department to a unit.
    public Guid? BusinessUnitId { get; set; }

    public decimal? BudgetMonthly { get; set; }

    public DepartmentStatus Status { get; set; } = DepartmentStatus.Active;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}
