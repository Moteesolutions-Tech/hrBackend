using Motee.Application.Common;
using Motee.Domain.Audit;

namespace Motee.Application.Audit;

// For the things a change tracker cannot see. Signing in writes nothing, downloading an
// export writes nothing, and opening a salary is a read — yet those are exactly the
// entries an auditor asks for.
//
// Entity changes are captured automatically and must not be recorded here as well, or
// every save produces two rows saying the same thing.
public interface IAuditTrail
{
    // tenantId is for events that happen before a tenant is established — signing in
    // is the case: there is no token yet, so nothing has resolved which company the
    // request belongs to, and an entry written without one is a row no screen can ever
    // show. Everything else leaves it null and lets the context stamp it.
    //
    // An event with no resolvable tenant is dropped rather than orphaned.
    void Record(
        AuditAction action,
        string module,
        string description,
        Guid? entityId = null,
        string? entityType = null,
        Guid? tenantId = null,

        // The outcome the caller knows. A failed sign-in is a 401 and must be
        // filterable as one; entries written by the interceptor set this themselves,
        // because a change that did not commit leaves no entry to label.
        int? httpStatus = null);

    Task<PagedResult<AuditEntryDto>> ListAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default);

    // What the filter dropdowns should offer. Served rather than hard-coded in the
    // client, because the set grows every time a module ships — a list maintained in
    // the frontend goes stale silently, and the symptom is a filter that cannot find
    // entries which plainly exist.
    Task<AuditCatalogueDto> CatalogueAsync(CancellationToken cancellationToken = default);
}

public sealed record AuditCatalogueDto
{
    // Every action the system can record, from the enum. Fixed, so it does not depend
    // on what happens to be in the table.
    public required IReadOnlyList<string> Actions { get; init; }

    // Every module the system has, from the same catalogue the access levels use, plus
    // anything recorded that is not in it yet. A module with no entries returns an empty
    // list — which is an answer ("nothing has happened here"), not a broken filter.
    public required IReadOnlyList<string> Modules { get; init; }

    // "2xx", "4xx", "5xx" — the classes present, so a filter option is offered only
    // when there is something to find.
    //
    // Written the way the filter shows it rather than as the leading digit. Returning
    // [2] made the client carry a rule that 2 means "2xx", which is the kind of
    // decoding a consumer should never have to know.
    public required IReadOnlyList<string> StatusClasses { get; init; }
}

public sealed record AuditQuery : PagedQuery
{
    public AuditAction? Action { get; init; }

    public string? Module { get; init; }

    public Guid? ActorUserId { get; init; }

    public Guid? EntityId { get; init; }

    // "2xx", "4xx", "5xx" — the class rather than the exact code, matching how the
    // screen filters. Exactly what the catalogue offers, so a client can send back what
    // it was given without translating it.
    public string? StatusClass { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public string? Search { get; init; }
}

public sealed record AuditEntryDto
{
    public required Guid Id { get; init; }

    public Guid? ActorUserId { get; init; }

    public string? ActorName { get; init; }

    public required AuditAction Action { get; init; }

    public required string Module { get; init; }

    public string? EntityType { get; init; }

    public Guid? EntityId { get; init; }

    public required string Description { get; init; }

    // The raw JSON as stored. Null where the policy withholds values, which the screen
    // shows as "changed" without a diff rather than as an empty one.
    public string? Changes { get; init; }

    public string? Endpoint { get; init; }

    public string? HttpMethod { get; init; }

    public int? HttpStatus { get; init; }

    public int? DurationMs { get; init; }

    public string? IpAddress { get; init; }

    public string? CorrelationId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
