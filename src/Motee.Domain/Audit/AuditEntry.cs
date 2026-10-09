using Motee.Domain.Common;

namespace Motee.Domain.Audit;

public enum AuditAction
{
    Create,
    Update,
    Delete,

    // Not entity writes, so nothing can infer them from a SaveChanges. They are
    // recorded explicitly by the code that performs them.
    Login,
    Logout,
    Export,
    View,
    Approve,
    Reject,
}

// One row per thing that happened. Written in the same transaction as the change it
// describes, so an audit trail cannot disagree with the data: either both land or
// neither does.
//
// Deliberately one flat table rather than a table per module. A new module writes here
// the day it ships without adding a table, a migration, or a line of audit code —
// which is the only version of this that survives the product growing.
public class AuditEntry : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // Null for anything the system did on its own — a background job, a scheduled
    // sweep. "Nobody" is a real answer and more honest than attributing it to whoever
    // last triggered the worker.
    public Guid? ActorUserId { get; set; }

    // Copied, not joined. A deleted or renamed user must not erase who did something
    // three years ago, which is the whole point of keeping the record.
    public string? ActorName { get; set; }

    public required AuditAction Action { get; set; }

    // The permission module this belongs to, so the screen can filter by the same
    // vocabulary the access levels use.
    public required string Module { get; set; }

    // The CLR type name — "Employee", "AccessLevel". Kept alongside the module because
    // one module writes several kinds of thing.
    public string? EntityType { get; set; }

    public Guid? EntityId { get; set; }

    // A sentence a person can read without opening the changes. Built at write time,
    // because reconstructing it later would need the data as it was then.
    public required string Description { get; set; }

    // Field-level before and after, as { "field": { "before": x, "after": y } }.
    //
    // Null where the policy forbids values — medical notes, bank details, identity
    // documents. That an employee's medical record changed is auditable; what it said
    // is not, or this table becomes the largest pile of sensitive data in the product
    // with none of the permission gates that protect the original.
    public string? Changes { get; set; }

    // The request that caused it. Null for background work, which has no request.
    public string? Endpoint { get; set; }

    public string? HttpMethod { get; set; }

    public int? HttpStatus { get; set; }

    public int? DurationMs { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    // Ties every entry from one request together, and lets the screen group a person's
    // activity without a sessions table.
    public string? CorrelationId { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }
}
