using Hangfire;
using Microsoft.EntityFrameworkCore;
using Motee.Application.Notifications;
using Motee.Application.Tenancy;
using Motee.Domain.Authorization;
using Motee.Domain.Contracts;
using Motee.Domain.Notifications;
using Motee.Infrastructure.Common;
using Motee.Infrastructure.Persistence;

namespace Motee.Api.Jobs;

// Tells somebody about the things that are quietly becoming true.
//
// Everything this raises is derived on read elsewhere — a contract is expiring,
// a document has lapsed — which is the right way to store it and the wrong way to
// find out about it. Derived means nobody is told, and a contract that lapses
// because nobody opened the right screen is the most expensive bug in the system.
//
// One job rather than one per condition. They share the same shape: find the
// rows, work out who should hear, raise one notification each. Seven jobs would
// be seven copies of the resolving and seven places to change the wording.
public sealed class ExpirySweepJob(
    MoteeDbContext dbContext,
    INotificationService notifications,
    TimeProvider timeProvider,
    ILogger<ExpirySweepJob> logger)
{
    public const string RecurringId = "expiry-sweep";

    private const string ContractsModule = "operations.contracts";

    private const string DocumentsModule = "operations.documents";

    // Retried a few times, then left. Unlike an email there is no deadline: the
    // sweep runs again tomorrow and the conditions will still be true, so a bad
    // night costs a day's notice rather than the notification itself.
    [AutomaticRetry(Attempts = 2)]
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        // Every tenant, because this runs on a worker that belongs to none.
        List<Guid> tenantIds = await dbContext.Tenants
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Select(tenant => tenant.Id)
            .ToListAsync(cancellationToken);

        int raised = 0;

        foreach (Guid tenantId in tenantIds)
        {
            // Scoped per tenant so the global filter does the work, rather than
            // every query here having to remember a tenant clause.
            using (AmbientTenant.Use(tenantId))
            {
                try
                {
                    raised += await SweepAsync(tenantId, cancellationToken);
                }
                catch (Exception exception)
                {
                    // One company's bad data must not stop the other hundred
                    // being told. Logged and carried on.
                    logger.LogError(
                        exception, "Expiry sweep failed for tenant {TenantId}.", tenantId);
                }
            }
        }

        logger.LogInformation(
            "Expiry sweep raised {Raised} notifications across {Tenants} tenants.",
            raised,
            tenantIds.Count);
    }

    private async Task<int> SweepAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        int raised = 0;

        // ── contracts ───────────────────────────────────────────────────────

        // Narrowed in SQL to the ones that could qualify, then judged in memory,
        // because the window depends on each contract's own notice period.
        List<Contract> ending = await dbContext.Contracts
            .AsNoTracking()
            .Where(contract => contract.EndDate != null
                && !contract.AutoRenew
                && contract.EndDate >= today
                && contract.Status != ContractStatus.Terminated
                && contract.Status != ContractStatus.Draft)
            .ToListAsync(cancellationToken);

        List<Contract> expiring =
        [
            .. ending.Where(contract =>
                Domain.Contracts.Contracts.IsExpiringSoon(contract, today)),
        ];

        if (expiring.Count > 0)
        {
            IReadOnlyList<Guid> audience =
                await AudienceAsync(tenantId, ContractsModule, cancellationToken);

            Dictionary<Guid, string> names = await NamesAsync(
                [.. expiring.Select(contract => contract.EmployeeId)], cancellationToken);

            foreach (Contract contract in expiring)
            {
                int days = contract.EndDate!.Value.DayNumber - today.DayNumber;

                foreach (Guid userId in audience)
                {
                    raised += await notifications.RaiseAsync(
                        new RaiseNotificationRequest
                        {
                            TenantId = tenantId,
                            UserId = userId,
                            Kind = NotificationKind.ContractExpiring,
                            Severity = NotificationSeverity.Warning,
                            Title = "Contract expiring",
                            Body =
                                $"{names.GetValueOrDefault(contract.EmployeeId) ?? "An employee"}"
                                + $"'s contract ends in {days} day{(days == 1 ? string.Empty : "s")}.",
                            Href = "/operations/contracts",
                            SubjectType = "contract",
                            SubjectId = contract.Id,
                        },
                        cancellationToken)
                        ? 1
                        : 0;
                }
            }
        }

        // ── documents ───────────────────────────────────────────────────────

        // Already past, not approaching. A document has no notice period to
        // measure a window from, and an expired certificate is the thing worth
        // saying rather than one that will expire.
        List<(Guid Id, string Name)> lapsed = await dbContext.HrDocuments
            .AsNoTracking()
            .Where(document => document.ExpiryDate != null
                && document.ExpiryDate < today
                && document.TrashedAt == null
                && !document.IsArchived)
            .Select(document => new ValueTuple<Guid, string>(document.Id, document.Name))
            .ToListAsync(cancellationToken);

        if (lapsed.Count == 0)
        {
            return raised;
        }

        IReadOnlyList<Guid> documentAudience =
            await AudienceAsync(tenantId, DocumentsModule, cancellationToken);

        foreach ((Guid id, string name) in lapsed)
        {
            foreach (Guid userId in documentAudience)
            {
                raised += await notifications.RaiseAsync(
                    new RaiseNotificationRequest
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        Kind = NotificationKind.DocumentExpiring,
                        Severity = NotificationSeverity.Warning,
                        Title = "Document expired",
                        Body = $"\"{name}\" is past its expiry date.",
                        Href = "/operations/documents",
                        SubjectType = "document",
                        SubjectId = id,
                    },
                    cancellationToken)
                    ? 1
                    : 0;
            }
        }

        return raised;
    }

    // Who should hear about this module: everybody holding an active level that
    // grants sight of it, plus the owner.
    //
    // The owner is added explicitly rather than found by permission. They hold no
    // access level at all since the separation — the bypass is what gives them
    // reach — so a query over levels would miss the one person who cannot be
    // locked out of their own company.
    //
    // Resolved now rather than at read time, so who was told is a recorded fact
    // instead of a query that changes answer when somebody's level is edited.
    private async Task<IReadOnlyList<Guid>> AudienceAsync(
        Guid tenantId,
        string module,
        CancellationToken cancellationToken)
    {
        // Permissions are jsonb behind a converter, so the module check cannot be
        // expressed in SQL. A company has a handful of levels, so they are
        // materialised and filtered here.
        List<AccessLevel> levels = await dbContext.AccessLevels
            .AsNoTracking()
            .Where(level => level.Status == AccessLevelStatus.Active)
            .ToListAsync(cancellationToken);

        List<Guid> granting =
        [
            .. levels
                .Where(level => level.Permissions.Any(permission =>
                    permission.Access
                    && permission.Module == module
                    && permission.Actions.Contains(PermissionAction.View)))
                .Select(level => level.Id),
        ];

        List<Guid> holders = granting.Count == 0
            ? []
            : await dbContext.UserAccessLevels
                .AsNoTracking()
                .Where(assignment => granting.Contains(assignment.AccessLevelId))
                .Select(assignment => assignment.UserId)
                .Distinct()
                .ToListAsync(cancellationToken);

        List<Guid> owners = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.TenantId == tenantId && user.IsOwner)
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

        return [.. holders.Concat(owners).Distinct()];
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(
        List<Guid> employeeIds,
        CancellationToken cancellationToken) =>
        await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employeeIds.Contains(employee.Id))
            .ToDictionaryAsync(
                employee => employee.Id,
                employee => employee.FirstName + " " + employee.LastName,
                cancellationToken);
}
