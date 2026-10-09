using Microsoft.EntityFrameworkCore;
using Motee.Application.Audit;
using Motee.Application.Common;
using Motee.Application.Tenancy;
using Motee.Domain.Audit;
using Motee.Domain.Authorization;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Audit;

internal sealed class AuditTrail(
    MoteeDbContext dbContext,
    IRequestContext requestContext,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : IAuditTrail
{
    // Added, not saved. The caller's own SaveChanges commits it, so recording a sign-in
    // cannot succeed while the thing it describes fails — and a read-only action like
    // View still needs an explicit save, which is why this returns void rather than
    // pretending to persist.
    public void Record(
        AuditAction action,
        string module,
        string description,
        Guid? entityId = null,
        string? entityType = null,
        Guid? tenantId = null,
        int? httpStatus = null)
    {
        Guid? resolved = tenantId ?? currentTenant.TenantId;

        // No tenant means no screen can ever show it. Signing in with an unknown address
        // is the case that reaches here: there is no user, so no company to file it
        // under. That belongs in the application log, which already records it, not in
        // a company's audit trail as an unreadable row.
        if (resolved is not Guid owner || owner == Guid.Empty)
        {
            return;
        }

        dbContext.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            TenantId = owner,
            ActorUserId = Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null,
            ActorName = requestContext.UserEmail,
            Action = action,
            Module = module,
            EntityType = entityType,
            EntityId = entityId,
            Description = description,
            Endpoint = requestContext.Endpoint,
            HttpMethod = requestContext.HttpMethod,
            HttpStatus = httpStatus,
            IpAddress = requestContext.IpAddress,
            UserAgent = requestContext.UserAgent,
            CorrelationId = requestContext.CorrelationId,
            CreatedAt = timeProvider.GetUtcNow(),
        });
    }

    public async Task<PagedResult<AuditEntryDto>> ListAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<AuditEntry> matching = Filter(query);

        int total = await matching.CountAsync(cancellationToken);

        List<AuditEntryDto> items = await matching
            // Newest first. An audit screen is opened to answer "what just happened",
            // not to read the beginning of time.
            .OrderByDescending(entry => entry.CreatedAt)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(entry => new AuditEntryDto
            {
                Id = entry.Id,
                ActorUserId = entry.ActorUserId,
                ActorName = entry.ActorName,
                Action = entry.Action,
                Module = entry.Module,
                EntityType = entry.EntityType,
                EntityId = entry.EntityId,
                Description = entry.Description,
                Changes = entry.Changes,
                Endpoint = entry.Endpoint,
                HttpMethod = entry.HttpMethod,
                HttpStatus = entry.HttpStatus,
                DurationMs = entry.DurationMs,
                IpAddress = entry.IpAddress,
                CorrelationId = entry.CorrelationId,
                CreatedAt = entry.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<AuditEntryDto>
        {
            Items = items,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<AuditCatalogueDto> CatalogueAsync(
        CancellationToken cancellationToken = default)
    {
        // The catalogue is the vocabulary, like the enum is for actions. A list built
        // from whatever happens to be in the table has two faults: on a new tenant it
        // shows two options and looks broken rather than empty, and it surfaces the
        // kebab-case fallback AuditPolicy produces for an unmapped entity —
        // "employee-invitation" is an internal name, not a module anyone should filter by.
        //
        // Unioned with what is actually recorded so nothing is unfilterable: a brand-new
        // entity writes entries before anyone adds it to AuditPolicy.Modules, and those
        // must still be findable.
        List<string> recorded = await dbContext.AuditEntries
            .AsNoTracking()
            .Select(entry => entry.Module)
            .Distinct()
            .ToListAsync(cancellationToken);

        List<string> modules = [.. ModuleCatalogue.All
            .Union(recorded, StringComparer.Ordinal)
            .OrderBy(module => module, StringComparer.Ordinal)];

        List<int> present = await dbContext.AuditEntries
            .AsNoTracking()
            .Where(entry => entry.HttpStatus != null)
            .Select(entry => entry.HttpStatus!.Value / 100)
            .Distinct()
            .OrderBy(statusClass => statusClass)
            .ToListAsync(cancellationToken);

        return new AuditCatalogueDto
        {
            Actions = [.. Enum.GetNames<AuditAction>().Select(name => name.ToLowerInvariant())],
            Modules = modules,

            // Rendered as the filter shows it. The division above is an implementation
            // detail of finding which classes exist, not a shape to hand a client.
            StatusClasses = [.. present.Select(statusClass => $"{statusClass}xx")],
        };
    }

    private IQueryable<AuditEntry> Filter(AuditQuery query)
    {
        IQueryable<AuditEntry> matching = dbContext.AuditEntries.AsNoTracking();

        if (query.Action is AuditAction action)
        {
            matching = matching.Where(entry => entry.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(query.Module))
        {
            matching = matching.Where(entry => entry.Module == query.Module);
        }

        if (query.ActorUserId is Guid actor)
        {
            matching = matching.Where(entry => entry.ActorUserId == actor);
        }

        // Everything that ever happened to one record, which is the question an auditor
        // actually asks: "show me this employee's history".
        if (query.EntityId is Guid entityId)
        {
            matching = matching.Where(entry => entry.EntityId == entityId);
        }

        // "2xx" → 200..299. An unparseable value filters nothing rather than throwing:
        // a filter is a narrowing, and a bad one should show everything rather than
        // fail the request the screen depends on.
        if (!string.IsNullOrWhiteSpace(query.StatusClass)
            && int.TryParse(query.StatusClass.AsSpan(0, 1), out int leadingDigit))
        {
            int lower = leadingDigit * 100;

            matching = matching.Where(entry =>
                entry.HttpStatus >= lower && entry.HttpStatus < lower + 100);
        }

        if (query.From is DateTimeOffset from)
        {
            matching = matching.Where(entry => entry.CreatedAt >= from);
        }

        if (query.To is DateTimeOffset to)
        {
            matching = matching.Where(entry => entry.CreatedAt <= to);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{Escape(query.Search.Trim())}%";

            matching = matching.Where(entry =>
                EF.Functions.ILike(entry.Description, pattern, @"\")
                || (entry.ActorName != null
                    && EF.Functions.ILike(entry.ActorName, pattern, @"\")));
        }

        return matching;
    }

    private static string Escape(string value) => value
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);
}
