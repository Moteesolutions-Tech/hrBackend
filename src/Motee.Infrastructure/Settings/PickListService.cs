using Microsoft.EntityFrameworkCore;
using Motee.Application.Audit;
using Motee.Application.Settings;
using Motee.Domain.Audit;
using Motee.Domain.Organisation;
using Motee.Domain.Settings;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Settings;

// The dropdown options a company configures.
//
// Department, Job Title and Location are read from departments, positions and
// branches — the tables that already own them — and refuse writes, naming the
// screen that does. Storing them again here would give the product two lists
// that disagree, and an option added in Settings would never appear on the
// employee form, so the dialog would look like it worked and change nothing.
internal sealed class PickListService(
    MoteeDbContext dbContext,
    IAuditTrail audit,
    TimeProvider timeProvider) : IPickListService
{
    private const string Module = "admin.settings";

    public async Task<IReadOnlyList<PickListView>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        List<PickListView> lists = [];

        foreach (PickListDefinition definition in PickListCatalogue.All)
        {
            lists.Add(await ViewAsync(definition, cancellationToken));
        }

        return lists;
    }

    public async Task<PickListView?> FindAsync(
        string key,
        CancellationToken cancellationToken = default) =>
        PickListCatalogue.Find(key) is PickListDefinition definition
            ? await ViewAsync(definition, cancellationToken)
            : null;

    public async Task<PickListResult> AddAsync(
        string key,
        PickListValueRequest request,
        CancellationToken cancellationToken = default)
    {
        if (PickListCatalogue.Find(key) is not PickListDefinition definition)
        {
            return PickListResult.Failed(SettingsOutcome.NotFound);
        }

        List<PickListOption> existing = await StoredAsync(definition.Key, cancellationToken);

        PickListRefusal refusal = PickListRules.CheckValue(definition, request.Value, existing);

        if (refusal != PickListRefusal.None)
        {
            return PickListResult.Refused(refusal);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        PickListOption option = new()
        {
            Id = Guid.NewGuid(),
            ListKey = definition.Key,
            Value = request.Value.Trim(),

            // The first option on an empty list becomes the default whatever
            // was asked for, and Settle below enforces it either way.
            IsDefault = existing.Count == 0 || request.IsDefault,
            Sequence = PickListRules.NextSequence(existing),
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.PickListOptions.Add(option);

        PickListRules.Settle([.. existing, option]);

        audit.Record(
            AuditAction.Create,
            Module,
            $"Added \"{option.Value}\" to {definition.Name}",
            entityId: option.Id,
            entityType: nameof(PickListOption));

        await dbContext.SaveChangesAsync(cancellationToken);

        return PickListResult.Ok(await ViewAsync(definition, cancellationToken));
    }

    public async Task<PickListResult> UpdateAsync(
        string key,
        Guid id,
        PickListValueRequest request,
        CancellationToken cancellationToken = default)
    {
        if (PickListCatalogue.Find(key) is not PickListDefinition definition)
        {
            return PickListResult.Failed(SettingsOutcome.NotFound);
        }

        List<PickListOption> existing = await StoredAsync(definition.Key, cancellationToken);

        if (existing.FirstOrDefault(option => option.Id == id) is not PickListOption target)
        {
            // An entity-backed list has no stored rows, so this is also where
            // "you cannot edit Department here" lands. The refusal says which
            // it is rather than reporting a missing option.
            return definition.Source == PickListSource.Configured
                ? PickListResult.Failed(SettingsOutcome.NotFound)
                : PickListResult.Refused(PickListRefusal.ListNotConfigurable);
        }

        PickListRefusal refusal =
            PickListRules.CheckValue(definition, request.Value, existing, editing: id);

        if (refusal != PickListRefusal.None)
        {
            return PickListResult.Refused(refusal);
        }

        string was = target.Value;

        target.Value = request.Value.Trim();
        target.UpdatedAt = timeProvider.GetUtcNow();

        audit.Record(
            AuditAction.Update,
            Module,
            $"Renamed \"{was}\" to \"{target.Value}\" in {definition.Name}",
            entityId: target.Id,
            entityType: nameof(PickListOption));

        await dbContext.SaveChangesAsync(cancellationToken);

        return PickListResult.Ok(await ViewAsync(definition, cancellationToken));
    }

    public async Task<PickListResult> RemoveAsync(
        string key,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (PickListCatalogue.Find(key) is not PickListDefinition definition)
        {
            return PickListResult.Failed(SettingsOutcome.NotFound);
        }

        if (definition.Source != PickListSource.Configured)
        {
            return PickListResult.Refused(PickListRefusal.ListNotConfigurable);
        }

        List<PickListOption> existing = await StoredAsync(definition.Key, cancellationToken);

        if (existing.FirstOrDefault(option => option.Id == id) is not PickListOption target)
        {
            return PickListResult.Failed(SettingsOutcome.NotFound);
        }

        dbContext.PickListOptions.Remove(target);

        // Removing the default promotes the next option, so a list never ends
        // up with options and no default.
        PickListRules.Settle([.. existing.Where(option => option.Id != id)]);

        audit.Record(
            AuditAction.Delete,
            Module,
            $"Removed \"{target.Value}\" from {definition.Name}",
            entityId: target.Id,
            entityType: nameof(PickListOption));

        await dbContext.SaveChangesAsync(cancellationToken);

        return PickListResult.Ok(await ViewAsync(definition, cancellationToken));
    }

    public async Task<PickListResult> SetDefaultAsync(
        string key,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (PickListCatalogue.Find(key) is not PickListDefinition definition)
        {
            return PickListResult.Failed(SettingsOutcome.NotFound);
        }

        if (definition.Source != PickListSource.Configured)
        {
            return PickListResult.Refused(PickListRefusal.ListNotConfigurable);
        }

        List<PickListOption> existing = await StoredAsync(definition.Key, cancellationToken);

        if (existing.FirstOrDefault(option => option.Id == id) is not PickListOption target)
        {
            return PickListResult.Failed(SettingsOutcome.NotFound);
        }

        foreach (PickListOption option in existing)
        {
            option.IsDefault = option.Id == id;
            option.UpdatedAt = timeProvider.GetUtcNow();
        }

        audit.Record(
            AuditAction.Update,
            Module,
            $"\"{target.Value}\" is now the default for {definition.Name}",
            entityId: target.Id,
            entityType: nameof(PickListOption));

        await dbContext.SaveChangesAsync(cancellationToken);

        return PickListResult.Ok(await ViewAsync(definition, cancellationToken));
    }

    // ── shared ─────────────────────────────────────────────────────────────

    private Task<List<PickListOption>> StoredAsync(
        string listKey,
        CancellationToken cancellationToken) =>
        dbContext.PickListOptions
            .Where(option => option.ListKey == listKey)
            .OrderBy(option => option.Sequence)
            .ToListAsync(cancellationToken);

    private async Task<PickListView> ViewAsync(
        PickListDefinition definition,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PickListOptionDto> options = definition.Source == PickListSource.Entity
            ? await BorrowedAsync(definition.Key, cancellationToken)
            : [
                .. (await StoredAsync(definition.Key, cancellationToken)).Select(option =>
                    new PickListOptionDto
                    {
                        Id = option.Id,
                        Value = option.Value,
                        IsDefault = option.IsDefault,
                    }),
            ];

        return new PickListView
        {
            Id = definition.Key,
            Name = definition.Name,
            Description = definition.Description,
            Options = options,
            Editable = definition.Source == PickListSource.Configured,
            OwnedBy = definition.OwnedBy,
            OwnerRoute = definition.OwnerRoute,
        };
    }

    // The real rows, so the screen shows what the employee form will actually
    // offer. Read-only, and no default: which department somebody joins is not
    // a dropdown preference.
    private async Task<IReadOnlyList<PickListOptionDto>> BorrowedAsync(
        string key,
        CancellationToken cancellationToken) => key switch
        {
            "department" =>
            [
                .. (await dbContext.Departments
                        .AsNoTracking()
                        .Where(department => department.Status == DepartmentStatus.Active)
                        .OrderBy(department => department.Name)
                        .Select(department => new { department.Id, Value = department.Name })
                        .ToListAsync(cancellationToken))
                    .Select(row => new PickListOptionDto
                    {
                        Id = row.Id,
                        Value = row.Value,
                        IsDefault = false,
                    }),
            ],
            "job-title" =>
            [
                .. (await dbContext.Positions
                        .AsNoTracking()
                        .Where(position => position.IsActive)
                        .OrderBy(position => position.Title)
                        .Select(position => new { position.Id, Value = position.Title })
                        .ToListAsync(cancellationToken))
                    .Select(row => new PickListOptionDto
                    {
                        Id = row.Id,
                        Value = row.Value,
                        IsDefault = false,
                    }),
            ],
            "location" =>
            [
                .. (await dbContext.Branches
                        .AsNoTracking()
                        .Where(branch => branch.Status == BranchStatus.Active)
                        .OrderBy(branch => branch.Name)
                        .Select(branch => new { branch.Id, Value = branch.Name })
                        .ToListAsync(cancellationToken))
                    .Select(row => new PickListOptionDto
                    {
                        Id = row.Id,
                        Value = row.Value,
                        IsDefault = false,
                    }),
            ],
            _ => [],
        };
}
