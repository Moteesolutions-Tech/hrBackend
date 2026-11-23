using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Projects;
using Motee.Domain.Projects;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Projects;

// The go-live readiness checklist: the template joined to this project's ticks.
//
// A project nobody has looked at still returns all twenty-five checks. Only the
// ticks are rows, so there is nothing to seed when a project is created and
// nothing to migrate when a check is reworded.
internal sealed class ProjectGoLiveService(
    MoteeDbContext dbContext,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IProjectGoLiveService
{
    public async Task<GoLiveChecklistDto?> GetAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.Projects.AnyAsync(
            project => project.Id == projectId, cancellationToken))
        {
            return null;
        }

        return await DescribeAsync(projectId, cancellationToken);
    }

    public async Task<GoLiveResult> SetAsync(
        Guid projectId,
        string itemKey,
        GoLiveTickRequest request,
        CancellationToken cancellationToken = default)
    {
        Project? project = await dbContext.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == projectId, cancellationToken);

        if (project is null)
        {
            return GoLiveResult.Failed(ProjectOutcome.NotFound);
        }

        string key = (itemKey ?? string.Empty).Trim();

        // A tick against a check that is not in the template would never be
        // read back, so it is refused rather than stored.
        if (!GoLiveTemplate.All.Any(item => string.Equals(item.Key, key, StringComparison.Ordinal)))
        {
            return GoLiveResult.Refused(ProjectRefusal.UnknownCheck);
        }

        GoLiveCompletion? completion = await dbContext.GoLiveCompletions
            .FirstOrDefaultAsync(
                row => row.ProjectId == projectId && row.ItemKey == key, cancellationToken);

        bool isNew = completion is null;

        completion ??= new GoLiveCompletion
        {
            Id = Guid.NewGuid(),
            TenantId = project.TenantId,
            ProjectId = projectId,
            ItemKey = key,
        };

        completion.IsDone = request.IsDone;
        completion.Notes = string.IsNullOrWhiteSpace(request.Notes)
            ? null
            : request.Notes.Trim();
        completion.UpdatedByUserId = UserId();
        completion.UpdatedAt = timeProvider.GetUtcNow();

        if (isNew)
        {
            dbContext.GoLiveCompletions.Add(completion);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return GoLiveResult.Ok(await DescribeAsync(projectId, cancellationToken));
    }

    private Guid? UserId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private async Task<GoLiveChecklistDto> DescribeAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        List<GoLiveCompletion> ticks = await dbContext.GoLiveCompletions
            .AsNoTracking()
            .Where(completion => completion.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        Dictionary<string, GoLiveCompletion> byKey = new(StringComparer.Ordinal);

        foreach (GoLiveCompletion tick in ticks)
        {
            byKey[tick.ItemKey] = tick;
        }

        return new GoLiveChecklistDto
        {
            ProjectId = projectId,
            Items =
            [
                .. GoLiveTemplate.All.Select(item =>
                {
                    GoLiveCompletion? tick = byKey.GetValueOrDefault(item.Key);

                    return new GoLiveCheckDto
                    {
                        Key = item.Key,
                        Category = item.Category,
                        Label = item.Label,
                        IsDone = tick?.IsDone ?? false,
                        Notes = tick?.Notes,
                        UpdatedByUserId = tick?.UpdatedByUserId,
                        UpdatedAt = tick?.UpdatedAt,
                    };
                }),
            ],
            Readiness = GoLiveTemplate.Readiness(
                [
                    .. ticks
                        .Where(tick => tick.IsDone)
                        .Select(tick => tick.ItemKey),
                ]),
        };
    }
}
