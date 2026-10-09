using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Motee.Application.Common;
using Motee.Application.Tenancy;
using Motee.Domain.Audit;
using Motee.Domain.Common;

namespace Motee.Infrastructure.Audit;

// Writes the audit trail from the change tracker, immediately before the same
// SaveChanges that persists the change.
//
// Two properties follow from doing it here rather than in each service:
//
//   A new module is audited the day its entity is added. Nothing in that module calls
//   anything, and nobody can forget to — which is what a per-service call always ends
//   up meaning once there are forty modules and several people.
//
//   The entry and the change commit together. An audit trail written afterwards can
//   disagree with the data when the second write fails; this one either lands with the
//   change or not at all.
internal sealed class AuditSaveChangesInterceptor(
    IRequestContext requestContext,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            Capture(eventData.Context);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    // The synchronous path exists because tests and tooling call SaveChanges directly,
    // and an audit trail that depends on which overload the caller used is not one.
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is not null)
        {
            Capture(eventData.Context);
        }

        return base.SavingChanges(eventData, result);
    }

    private void Capture(DbContext context)
    {
        // Materialised before adding anything: adding to the change tracker while
        // enumerating it throws, and the entries being added are themselves entities.
        List<EntityEntry> tracked = [.. context.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added
                or EntityState.Modified
                or EntityState.Deleted)];

        List<AuditEntry> entries = [];

        foreach (EntityEntry entry in tracked)
        {
            string entityType = entry.Entity.GetType().Name;

            if (!AuditPolicy.IsAudited(entityType))
            {
                continue;
            }

            AuditEntry? audit = Describe(entry, entityType);

            if (audit is null)
            {
                continue;
            }

            // Stamped here rather than left to the context. MoteeDbContext.StampTenant
            // runs before base.SaveChanges, and this interceptor runs inside it — so an
            // entry added now has already missed the stamp and would be written with an
            // empty tenant, invisible to the query filter for ever.
            //
            // Falling back to the entity's own tenant covers work done with no request:
            // a background job, or registration creating the first user of a tenant
            // nobody is signed into yet.
            Guid? tenantId = currentTenant.TenantId
                ?? (entry.Entity as ITenantScoped)?.TenantId;

            if (tenantId is not Guid resolved || resolved == Guid.Empty)
            {
                // Nothing to file it under. A row no tenant can read is worse than no
                // row: it is invisible on the screen and still holds the data.
                continue;
            }

            audit.TenantId = resolved;
            entries.Add(audit);
        }

        foreach (AuditEntry entry in entries)
        {
            context.Add(entry);
        }
    }

    private AuditEntry? Describe(EntityEntry entry, string entityType)
    {
        AuditAction action = entry.State switch
        {
            EntityState.Added => AuditAction.Create,
            EntityState.Modified => AuditAction.Update,
            EntityState.Deleted => AuditAction.Delete,
            _ => AuditAction.Update,
        };

        string? changes = null;

        if (action == AuditAction.Update)
        {
            List<PropertyEntry> changed = [.. Changed(entry)];

            // An update where nothing actually changed produces no entry. EF marks an
            // entity Modified when it is attached and written back unchanged, and a
            // trail full of "updated, nothing different" is one nobody reads.
            if (changed.Count == 0)
            {
                return null;
            }

            // Withholding the values must not withhold the entry. That an employee's
            // medical record was changed, by whom and when, is precisely what an
            // auditor needs; only the content is off limits. Conflating the two — as
            // the first version of this did — silently made those records the one thing
            // in the product with no history at all.
            changes = AuditPolicy.RecordsValues(entityType)
                ? Serialise(changed)
                : null;
        }

        return new AuditEntry
        {
            Id = Guid.NewGuid(),
            ActorUserId = Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null,
            ActorName = requestContext.UserEmail,
            Action = action,
            Module = AuditPolicy.ModuleFor(entityType),
            EntityType = entityType,
            EntityId = KeyOf(entry),
            Description = $"{action} {entityType}",
            Changes = changes,
            Endpoint = requestContext.Endpoint,
            HttpMethod = requestContext.HttpMethod,

            // A change entry only exists when the change commits: a failed request rolls
            // the transaction back and takes this row with it. So an entry written here
            // is by definition a success, and the status is knowable now rather than
            // needing a second write after the response.
            //
            // Null with no request at all, which is background work — a job has no HTTP
            // status and inventing one would make the screen's filter lie.
            HttpStatus = requestContext.Endpoint is null
                ? null
                : action == AuditAction.Create ? 201 : 200,

            IpAddress = requestContext.IpAddress,
            UserAgent = requestContext.UserAgent,
            CorrelationId = requestContext.CorrelationId,
            CreatedAt = timeProvider.GetUtcNow(),

            // TenantId is set by the caller above, not here and not by the context —
            // see the note there about StampTenant having already run.
        };
    }

    // What genuinely moved. Separate from serialising it, because whether anything
    // changed and whether we may say what it changed to are different questions.
    private static IEnumerable<PropertyEntry> Changed(EntityEntry entry) =>
        entry.Properties.Where(property =>
            property.IsModified

            // Every service stamps UpdatedAt, so counting it would make every save look
            // like a change and no update would ever be "nothing happened".
            && !AuditPolicy.IsBookkeeping(property.Metadata.Name)

            // Equal values still arrive as modified when an entity is attached and
            // written back, so comparing is what keeps the trail meaningful.
            && !Equals(property.OriginalValue, property.CurrentValue));

    private static string Serialise(IEnumerable<PropertyEntry> changed)
    {
        Dictionary<string, object?> diff = [];

        foreach (PropertyEntry property in changed)
        {
            string name = property.Metadata.Name;

            diff[name] = AuditPolicy.IsRedacted(name)
                ? new { before = "[redacted]", after = "[redacted]" }
                : new { before = property.OriginalValue, after = property.CurrentValue };
        }

        return JsonSerializer.Serialize(diff, SerializerOptions);
    }

    private static Guid? KeyOf(EntityEntry entry)
    {
        PropertyEntry? key = entry.Properties
            .FirstOrDefault(property => property.Metadata.IsPrimaryKey());

        return key?.CurrentValue as Guid?;
    }
}
