using Microsoft.EntityFrameworkCore;
using Motee.Domain.Authorization;
using Motee.Domain.Identity;
using Motee.Domain.Workflows;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Workflows;

// Writes the four workflows the product drives itself.
//
// Seeded as System, so a company cannot delete the flows its own recruitment
// and onboarding screens fire — it can archive them, which is the honest way to
// say "we do not use this".
//
// They start Active, unlike a workflow somebody writes, because the events that
// trigger them are live from day one.
internal sealed class WorkflowSeeder(MoteeDbContext dbContext, TimeProvider timeProvider)
{
    // The access levels are passed in rather than read back.
    //
    // Registration seeds the levels and this in one transaction, so a query
    // would miss rows that are added but not yet saved — and every task would
    // be assigned to nobody. The levels are already in hand at the one call
    // site, so handing them over is both correct and cheaper.
    public async Task SeedAsync(
        Guid tenantId,
        IReadOnlyList<AccessLevel> accessLevels,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, Guid> levels = [];

        foreach (AccessLevel level in accessLevels.Where(level =>
            level.TenantId == tenantId && level.TemplateSlug is not null))
        {
            levels[level.TemplateSlug!] = level.Id;
        }

        List<string> present = await dbContext.Workflows
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(workflow => workflow.TenantId == tenantId
                && workflow.WorkflowKind == WorkflowKind.System)
            .Select(workflow => workflow.Title)
            .ToListAsync(cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        foreach (WorkflowSeed seed in WorkflowSeeds.All)
        {
            // Re-seeding a company that already has them is a no-op rather than
            // a second copy.
            if (present.Contains(seed.Title, StringComparer.Ordinal))
            {
                continue;
            }

            Workflow workflow = new()
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Title = seed.Title,
                Description = seed.Description,
                TriggerMode = WorkflowTriggerMode.Automatic,
                ScheduleKind = ScheduleKind.Relative,
                TriggerEvent = seed.TriggerEvent,

                // As the event lands. The per-task day offsets carry the rest
                // of the timing.
                OffsetValue = 0,
                OffsetUnit = ScheduleOffsetUnit.Days,
                ScopeKind = WorkflowScopeKind.All,
                WorkflowKind = WorkflowKind.System,
                Status = WorkflowStatus.Active,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now,
            };

            dbContext.Workflows.Add(workflow);
            dbContext.WorkflowTasks.AddRange(Build(workflow, seed, levels));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static List<WorkflowTask> Build(
        Workflow workflow,
        WorkflowSeed seed,
        Dictionary<string, Guid> levels)
    {
        // Ids first, so the step numbers in the seed can be resolved against
        // the final list.
        IReadOnlyList<Guid> ids = [.. seed.Tasks.Select(_ => Guid.NewGuid())];

        return
        [
            .. seed.Tasks.Select((task, index) => new WorkflowTask
            {
                Id = ids[index],
                TenantId = workflow.TenantId,
                WorkflowId = workflow.Id,
                Sequence = index + 1,
                Title = task.Title,
                Description = task.Description,
                AssigneeKind = AssigneeKind.AccessLevel,
                AssigneeAccessLevelId = LevelFor(levels, task.Assignee),
                HasReviewer = task.Reviewer is not null,
                ReviewerAccessLevelId = task.Reviewer is Role reviewer
                    ? LevelFor(levels, reviewer)
                    : null,
                DueDayOffset = task.DueDayOffset,
                ExpectedDurationDays = task.ExpectedDurationDays,
                Priority = task.Priority,
                EscalateAfterDays = task.EscalateAfterDays,
                DependsOn = [.. task.DependsOnSteps.Select(step => ids[step - 1])],
                ParallelGroup = task.ParallelGroup,
                Condition = task.Condition,
                ConditionHolds = task.ConditionHolds,
            }),
        ];
    }

    private static Guid? LevelFor(Dictionary<string, Guid> levels, Role role) =>
        levels.TryGetValue(Roles.ToSlug(role), out Guid id) ? id : null;
}
