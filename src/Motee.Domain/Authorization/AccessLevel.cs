using Motee.Domain.Common;

namespace Motee.Domain.Authorization;

public enum AccessLevelKind
{
    // Created for every tenant from the shipped templates. Renameable, editable, but
    // it is what new tenants start with.
    Default,

    // Built by the tenant.
    Custom,
}

public enum AccessLevelStatus
{
    Draft,
    Active,

    // Withdrawn. Cannot be assigned, and grants nothing to anyone still holding it —
    // otherwise deactivating a level would be useless as a way to stop access.
    Inactive,
}

// A named set of permissions a tenant owns and edits. What used to be a fixed enum of
// roles: the enum survives only as the template each tenant's defaults are built
// from.
public class AccessLevel : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // Stable identity for a seeded level, e.g. "HR-ADMIN". Null for tenant-created
    // ones. Kept so a template can be recognised after it has been renamed, and so
    // registration can find the level it should assign the founder.
    public string? TemplateSlug { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public AccessLevelKind Kind { get; set; } = AccessLevelKind.Custom;

    public AccessLevelStatus Status { get; set; } = AccessLevelStatus.Draft;

    // Serialised as jsonb. One scope for the level, applied on top of module access:
    // the matrix answers "can they open Employees", this answers "whose records come
    // back".
    public required DataScope Scope { get; set; }

    public required IReadOnlyList<ModulePermission> Permissions { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    // Set when a session is issued carrying this level. A level nobody has used for a
    // year is the one safe to retire, and guessing from assignment counts alone does
    // not show that.
    public DateTimeOffset? LastUsedAt { get; set; }

    // Only an active level grants anything. Draft is unfinished; inactive is
    // withdrawn, and withdrawing has to take effect at once or it is not a control.
    public bool Grants => Status == AccessLevelStatus.Active;
}

// Which levels a person holds. Many-to-many: someone can be both a Line Manager and
// a Recruiter, and the two sets combine.
public class UserAccessLevel : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public Guid AccessLevelId { get; set; }

    public DateTimeOffset AssignedAt { get; set; }

    public Guid? AssignedByUserId { get; set; }
}
