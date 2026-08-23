using Motee.Domain.Common;

namespace Motee.Domain.Organisation;

// A grouping above departments — a division, a region, a company within a group.
// Exists because an access level can be confined to one: "this HR Manager covers
// Corporate Services", which departments alone cannot express when Corporate
// Services spans six of them.
//
// An entity rather than a label on a department, so renaming one does not silently
// change who an access level reaches.
public class BusinessUnit : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    // The tenant's own short form, as it appears in finance systems. Optional: not
    // every company codes them.
    public string? Code { get; set; }

    public string? Description { get; set; }

    // Retired rather than deleted. An access level scoped to it, or a department
    // sitting under it, both outlive the decision to stop using it.
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
