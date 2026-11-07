using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Recognition;
using Motee.Application.Tenancy;
using Motee.Domain.Employees;
using Motee.Domain.Recognition;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Recognition;

// Kudos.
//
// Visibility is enforced here rather than left to the client: a private kudos
// never leaves the service unless the caller is the sender, the recipient, or
// a member of the department it was addressed to. The client's own model
// carries two contradictory booleans for this, so a flag on the wire would be
// a flag somebody eventually honours wrongly.
//
// Reaction counts and comment counts are read from their rows. Nothing is
// incremented, so nothing can drift.
internal sealed class KudosService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentEmployee currentEmployee,
    TimeProvider timeProvider) : IKudosService
{
    private const int WallTake = 10;

    private string? _zone;

    public async Task<PagedResult<KudosPostDto>> WallAsync(
        KudosQuery query,
        CancellationToken cancellationToken = default)
    {
        Viewer viewer = await ViewerAsync(cancellationToken);

        IQueryable<KudosPost> posts = Visible(viewer);

        if (query.KudosType is KudosType type)
        {
            posts = posts.Where(post => post.KudosType == type);
        }

        if (query.CompanyValue is CompanyValue value)
        {
            posts = posts.Where(post => post.CompanyValue == value);
        }

        if (query.RecipientEmployeeId is Guid recipientId)
        {
            posts = posts.Where(post => post.RecipientEmployeeId == recipientId);
        }

        if (query.SenderEmployeeId is Guid senderId)
        {
            posts = posts.Where(post => post.SenderEmployeeId == senderId);
        }

        if (query.PinnedOnly == true)
        {
            posts = posts.Where(post => post.IsPinned);
        }

        if (!string.IsNullOrWhiteSpace(query.Month)
            && TryMonth(query.Month, out DateOnly from, out DateOnly until))
        {
            DateTimeOffset start = new(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            DateTimeOffset end = new(until.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            posts = posts.Where(post => post.CreatedAt >= start && post.CreatedAt < end);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{Escaped(query.Search.Trim())}%";

            posts = posts.Where(post =>
                EF.Functions.ILike(post.Message, pattern, "\\")
                || (post.CustomTypeName != null
                    && EF.Functions.ILike(post.CustomTypeName, pattern, "\\")));
        }

        int total = await posts.CountAsync(cancellationToken);

        List<KudosPost> page = await posts
            .OrderByDescending(post => post.IsPinned)
            .ThenByDescending(post => post.CreatedAt)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<KudosPostDto>
        {
            Items = await DescribeAsync(page, viewer, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<KudosPostDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Viewer viewer = await ViewerAsync(cancellationToken);

        KudosPost? post = await Visible(viewer)
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        return post is null ? null : (await DescribeAsync([post], viewer, cancellationToken))[0];
    }

    public async Task<KudosTally> SummaryAsync(CancellationToken cancellationToken = default)
    {
        Viewer viewer = await ViewerAsync(cancellationToken);

        List<KudosFacts> facts = await FactsAsync(viewer, cancellationToken);

        // The real headcount, not the client's hardcoded 156.
        int headcount = await dbContext.Employees
            .CountAsync(
                employee => EmployeeLifecycle.InPost.Contains(employee.Status),
                cancellationToken);

        return KudosWall.Of(facts, headcount, await TodayAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<KudosStandingDto>> LeaderboardAsync(
        int take = WallTake,
        CancellationToken cancellationToken = default)
    {
        Viewer viewer = await ViewerAsync(cancellationToken);

        List<KudosFacts> facts = await FactsAsync(viewer, cancellationToken);

        IReadOnlyList<KudosStanding> board = KudosWall.Leaderboard(
            facts, await TodayAsync(cancellationToken), Math.Clamp(take, 1, 50));

        Dictionary<Guid, Person> people = await PeopleAsync(
            [.. board.Select(entry => entry.EmployeeId)], cancellationToken);

        return
        [
            .. board.Select(entry => new KudosStandingDto
            {
                Standing = entry,
                EmployeeName = people.GetValueOrDefault(entry.EmployeeId)?.Name,
                DepartmentName = people.GetValueOrDefault(entry.EmployeeId)?.DepartmentName,
            }),
        ];
    }

    public async Task<KudosForMeDto> MineAsync(CancellationToken cancellationToken = default)
    {
        Viewer viewer = await ViewerAsync(cancellationToken);

        if (viewer.EmployeeId is not Guid me)
        {
            return new KudosForMeDto
            {
                Received = [],
                Sent = [],
                ReceivedCount = 0,
                SentCount = 0,
                Streak = 0,
            };
        }

        List<KudosPost> received = await Visible(viewer)
            .Where(post => post.RecipientEmployeeId == me)
            .OrderByDescending(post => post.CreatedAt)
            .Take(WallTake)
            .ToListAsync(cancellationToken);

        List<KudosPost> sent = await Visible(viewer)
            .Where(post => post.SenderEmployeeId == me)
            .OrderByDescending(post => post.CreatedAt)
            .Take(WallTake)
            .ToListAsync(cancellationToken);

        int receivedCount = await dbContext.KudosPosts
            .CountAsync(post => post.RecipientEmployeeId == me, cancellationToken);

        int sentCount = await dbContext.KudosPosts
            .CountAsync(post => post.SenderEmployeeId == me, cancellationToken);

        IReadOnlyList<KudosStanding> board = KudosWall.Leaderboard(
            await FactsAsync(viewer, cancellationToken),
            await TodayAsync(cancellationToken),

            // Everybody, because the person asking may be nowhere near the top
            // and still has a streak of their own.
            take: int.MaxValue);

        return new KudosForMeDto
        {
            Received = await DescribeAsync(received, viewer, cancellationToken),
            Sent = await DescribeAsync(sent, viewer, cancellationToken),
            ReceivedCount = receivedCount,
            SentCount = sentCount,
            Streak = board.FirstOrDefault(entry => entry.EmployeeId == me)?.Streak ?? 0,
        };
    }

    public async Task<KudosResult> SendAsync(
        KudosRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return KudosResult.Failed(KudosOutcome.NoEmployeeRecord);
        }

        if (request.RecipientEmployeeId is Guid recipientId)
        {
            bool employed = await dbContext.Employees.AnyAsync(
                employee => employee.Id == recipientId
                    && employee.Status != EmployeeStatus.Deleted,
                cancellationToken);

            if (!employed)
            {
                return KudosResult.Failed(KudosOutcome.EmployeeNotFound);
            }
        }

        if (request.RecipientDepartmentId is Guid departmentId)
        {
            bool exists = await dbContext.Departments.AnyAsync(
                department => department.Id == departmentId, cancellationToken);

            if (!exists)
            {
                return KudosResult.Failed(KudosOutcome.DepartmentNotFound);
            }
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        KudosPost post = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            SenderEmployeeId = me,
            RecipientEmployeeId = request.RecipientEmployeeId,
            RecipientDepartmentId = request.RecipientDepartmentId,
            KudosType = request.KudosType,
            CustomTypeName = Blank(request.CustomTypeName),
            CompanyValue = request.CompanyValue,
            Message = request.Message?.Trim() ?? string.Empty,
            Visibility = request.Visibility,
            CreatedAt = now,
            UpdatedAt = now,
        };

        KudosRefusal refusal = KudosPosts.Refusal(post);

        if (refusal != KudosRefusal.None)
        {
            return KudosResult.Refused(refusal);
        }

        dbContext.KudosPosts.Add(post);

        await dbContext.SaveChangesAsync(cancellationToken);

        return KudosResult.Ok(
            (await DescribeAsync([post], await ViewerAsync(cancellationToken), cancellationToken))[0]);
    }

    public async Task<KudosResult> RemoveAsync(
        Guid id,
        bool canRemoveAnybodys,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return KudosResult.Failed(KudosOutcome.NoEmployeeRecord);
        }

        KudosPost? post = await dbContext.KudosPosts
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (post is null)
        {
            return KudosResult.Failed(KudosOutcome.NotFound);
        }

        // Whoever wrote it may withdraw it. Taking down somebody else's needs
        // the module's Delete permission, which is a moderation decision.
        if (post.SenderEmployeeId != me && !canRemoveAnybodys)
        {
            return KudosResult.Failed(KudosOutcome.Forbidden);
        }

        dbContext.KudosPosts.Remove(post);

        await dbContext.SaveChangesAsync(cancellationToken);

        return KudosResult.Done();
    }

    public async Task<KudosResult> SetPinnedAsync(
        Guid id,
        bool pinned,
        CancellationToken cancellationToken = default)
    {
        KudosPost? post = await dbContext.KudosPosts
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (post is null)
        {
            return KudosResult.Failed(KudosOutcome.NotFound);
        }

        post.IsPinned = pinned;
        post.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return KudosResult.Ok(
            (await DescribeAsync([post], await ViewerAsync(cancellationToken), cancellationToken))[0]);
    }

    public async Task<KudosResult> ToggleReactionAsync(
        Guid id,
        ReactionType type,
        CancellationToken cancellationToken = default)
    {
        (KudosPost? post, Viewer viewer, KudosOutcome outcome) =
            await VisibleOrRefusedAsync(id, cancellationToken);

        if (post is null || viewer.EmployeeId is not Guid me)
        {
            return KudosResult.Failed(outcome);
        }

        KudosReaction? existing = await dbContext.KudosReactions
            .FirstOrDefaultAsync(
                row => row.KudosPostId == id && row.EmployeeId == me && row.Type == type,
                cancellationToken);

        // The feed has one handler for this, so the same click has to take the
        // reaction back.
        if (existing is not null)
        {
            dbContext.KudosReactions.Remove(existing);
        }
        else
        {
            dbContext.KudosReactions.Add(new KudosReaction
            {
                Id = Guid.NewGuid(),
                TenantId = post.TenantId,
                KudosPostId = id,
                EmployeeId = me,
                Type = type,
                CreatedAt = timeProvider.GetUtcNow(),
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return KudosResult.Ok((await DescribeAsync([post], viewer, cancellationToken))[0]);
    }

    public async Task<KudosResult> CommentAsync(
        Guid id,
        string message,
        CancellationToken cancellationToken = default)
    {
        (KudosPost? post, Viewer viewer, KudosOutcome outcome) =
            await VisibleOrRefusedAsync(id, cancellationToken);

        if (post is null || viewer.EmployeeId is not Guid me)
        {
            return KudosResult.Failed(outcome);
        }

        KudosRefusal refusal = KudosPosts.CommentRefusal(message);

        if (refusal != KudosRefusal.None)
        {
            return KudosResult.Refused(refusal);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        dbContext.KudosComments.Add(new KudosComment
        {
            Id = Guid.NewGuid(),
            TenantId = post.TenantId,
            KudosPostId = id,
            AuthorEmployeeId = me,
            Message = message.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return KudosResult.Ok((await DescribeAsync([post], viewer, cancellationToken))[0]);
    }

    public async Task<KudosResult> RemoveCommentAsync(
        Guid id,
        Guid commentId,
        bool canRemoveAnybodys,
        CancellationToken cancellationToken = default)
    {
        (KudosPost? post, Viewer viewer, KudosOutcome outcome) =
            await VisibleOrRefusedAsync(id, cancellationToken);

        if (post is null || viewer.EmployeeId is not Guid me)
        {
            return KudosResult.Failed(outcome);
        }

        KudosComment? comment = await dbContext.KudosComments
            .FirstOrDefaultAsync(
                row => row.Id == commentId && row.KudosPostId == id, cancellationToken);

        if (comment is null)
        {
            return KudosResult.Failed(KudosOutcome.NotFound);
        }

        if (comment.AuthorEmployeeId != me && !canRemoveAnybodys)
        {
            return KudosResult.Failed(KudosOutcome.Forbidden);
        }

        dbContext.KudosComments.Remove(comment);

        await dbContext.SaveChangesAsync(cancellationToken);

        return KudosResult.Ok((await DescribeAsync([post], viewer, cancellationToken))[0]);
    }

    // ── shared ──────────────────────────────────────────────────────────────

    // The visibility rule, as a query rather than a filter applied afterwards:
    // a private kudos never leaves the database unless the caller is party to
    // it. Every read goes through here.
    private IQueryable<KudosPost> Visible(Viewer viewer) =>
        dbContext.KudosPosts
            .AsNoTracking()
            .Where(post => post.Visibility != KudosVisibility.Private
                || post.SenderEmployeeId == viewer.EmployeeId
                || post.RecipientEmployeeId == viewer.EmployeeId
                || (post.RecipientDepartmentId != null
                    && post.RecipientDepartmentId == viewer.DepartmentId));

    private async Task<(KudosPost? Post, Viewer Viewer, KudosOutcome Outcome)>
        VisibleOrRefusedAsync(Guid id, CancellationToken cancellationToken)
    {
        Viewer viewer = await ViewerAsync(cancellationToken);

        if (viewer.EmployeeId is null)
        {
            return (null, viewer, KudosOutcome.NoEmployeeRecord);
        }

        KudosPost? post = await dbContext.KudosPosts
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (post is null)
        {
            return (null, viewer, KudosOutcome.NotFound);
        }

        // Reacting to something you cannot see would tell you it exists.
        return KudosPosts.CanSee(post, viewer.EmployeeId.Value, viewer.DepartmentId)
            ? (post, viewer, KudosOutcome.Succeeded)
            : (null, viewer, KudosOutcome.NotFound);
    }

    private async Task<List<KudosFacts>> FactsAsync(
        Viewer viewer,
        CancellationToken cancellationToken)
    {
        // The figures are over what the caller can see, so a private kudos
        // between two other people never moves anybody's leaderboard.
        List<Fact> rows = await Visible(viewer)
            .Select(post => new Fact(
                post.SenderEmployeeId,
                post.RecipientEmployeeId,
                post.KudosType,
                post.CompanyValue,
                post.CreatedAt))
            .ToListAsync(cancellationToken);

        string? zone = await ZoneAsync(cancellationToken);

        return
        [
            .. rows.Select(row => new KudosFacts
            {
                SenderEmployeeId = row.SenderEmployeeId,
                RecipientEmployeeId = row.RecipientEmployeeId,
                KudosType = row.KudosType,
                CompanyValue = row.CompanyValue,
                SentOn = TenantClock.DayOf(row.CreatedAt, zone),
            }),
        ];
    }

    private async Task<List<KudosPostDto>> DescribeAsync(
        IReadOnlyList<KudosPost> posts,
        Viewer viewer,
        CancellationToken cancellationToken)
    {
        if (posts.Count == 0)
        {
            return [];
        }

        List<Guid> postIds = [.. posts.Select(post => post.Id)];

        List<KudosReaction> reactions = await dbContext.KudosReactions
            .AsNoTracking()
            .Where(row => postIds.Contains(row.KudosPostId))
            .ToListAsync(cancellationToken);

        List<KudosComment> comments = await dbContext.KudosComments
            .AsNoTracking()
            .Where(row => postIds.Contains(row.KudosPostId))
            .OrderBy(row => row.CreatedAt)
            .ToListAsync(cancellationToken);

        List<Guid> employeeIds =
        [
            .. posts.Select(post => post.SenderEmployeeId)
                .Concat(posts.Where(post => post.RecipientEmployeeId is not null)
                    .Select(post => post.RecipientEmployeeId!.Value))
                .Concat(comments.Select(comment => comment.AuthorEmployeeId))
                .Distinct(),
        ];

        Dictionary<Guid, Person> people = await PeopleAsync(employeeIds, cancellationToken);

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id,
                department => department.Name,
                cancellationToken);

        return
        [
            .. posts.Select(post => new KudosPostDto
            {
                Id = post.Id,
                SenderEmployeeId = post.SenderEmployeeId,
                SenderName = people.GetValueOrDefault(post.SenderEmployeeId)?.Name,
                SenderDepartmentName =
                    people.GetValueOrDefault(post.SenderEmployeeId)?.DepartmentName,
                RecipientEmployeeId = post.RecipientEmployeeId,
                RecipientName = post.RecipientEmployeeId is Guid recipient
                    ? people.GetValueOrDefault(recipient)?.Name
                    : null,
                RecipientDepartmentId = post.RecipientDepartmentId,
                RecipientDepartmentName = post.RecipientDepartmentId is Guid departmentId
                    ? departments.GetValueOrDefault(departmentId)
                    : null,
                RecipientKind = KudosPosts.RecipientKindOf(post),
                KudosType = post.KudosType,
                CustomTypeName = post.CustomTypeName,
                CompanyValue = post.CompanyValue,
                Message = post.Message,
                Visibility = post.Visibility,
                IsAnnounced = KudosPosts.IsAnnounced(post),
                IsPinned = post.IsPinned,

                // Every reaction type, so the feed can draw the whole row
                // without inventing the zeroes itself.
                Reactions =
                [
                    .. Enum.GetValues<ReactionType>().Select(type => new KudosReactionDto
                    {
                        Type = type,
                        Count = reactions.Count(row =>
                            row.KudosPostId == post.Id && row.Type == type),
                        Mine = reactions.Any(row =>
                            row.KudosPostId == post.Id
                            && row.Type == type
                            && row.EmployeeId == viewer.EmployeeId),
                    }),
                ],
                Comments =
                [
                    .. comments
                        .Where(comment => comment.KudosPostId == post.Id)
                        .Select(comment => new KudosCommentDto
                        {
                            Id = comment.Id,
                            AuthorEmployeeId = comment.AuthorEmployeeId,
                            AuthorName = people.GetValueOrDefault(comment.AuthorEmployeeId)?.Name,
                            AuthorDepartmentName = people
                                .GetValueOrDefault(comment.AuthorEmployeeId)?.DepartmentName,
                            Message = comment.Message,
                            CanRemove = comment.AuthorEmployeeId == viewer.EmployeeId,
                            CreatedAt = comment.CreatedAt,
                        }),
                ],
                CanRemove = post.SenderEmployeeId == viewer.EmployeeId,
                CreatedAt = post.CreatedAt,
            }),
        ];
    }

    private async Task<Dictionary<Guid, Person>> PeopleAsync(
        IReadOnlyList<Guid> employeeIds,
        CancellationToken cancellationToken)
    {
        if (employeeIds.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id,
                department => department.Name,
                cancellationToken);

        return await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employeeIds.Contains(employee.Id))
            .Select(employee => new
            {
                employee.Id,
                Name = employee.FirstName + " " + employee.LastName,
                employee.DepartmentId,
            })
            .ToDictionaryAsync(
                row => row.Id,
                row => new Person(
                    row.Name,
                    row.DepartmentId is Guid id ? departments.GetValueOrDefault(id) : null),
                cancellationToken);
    }

    private async Task<Viewer> ViewerAsync(CancellationToken cancellationToken)
    {
        Guid? employeeId = await currentEmployee.IdAsync(cancellationToken);

        if (employeeId is not Guid id)
        {
            return new Viewer(null, null);
        }

        Guid? departmentId = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == id)
            .Select(employee => employee.DepartmentId)
            .FirstOrDefaultAsync(cancellationToken);

        return new Viewer(id, departmentId);
    }

    // "2026-04" to the half-open range that month covers.
    private static bool TryMonth(string month, out DateOnly from, out DateOnly until)
    {
        from = default;
        until = default;

        string[] parts = month.Trim().Split('-');

        if (parts.Length != 2
            || !int.TryParse(parts[0], out int year)
            || !int.TryParse(parts[1], out int number)
            || number is < 1 or > 12)
        {
            return false;
        }

        from = new DateOnly(year, number, 1);
        until = from.AddMonths(1);

        return true;
    }

    private static string Escaped(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken) =>
        TenantClock.DayOf(timeProvider.GetUtcNow(), await ZoneAsync(cancellationToken));

    private async Task<string?> ZoneAsync(CancellationToken cancellationToken)
    {
        if (_zone is not null)
        {
            return _zone;
        }

        if (currentTenant.TenantId is not Guid tenantId)
        {
            return null;
        }

        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        _zone = settings?.TimeZoneId;

        return _zone;
    }

    private sealed record Viewer(Guid? EmployeeId, Guid? DepartmentId);

    private sealed record Person(string Name, string? DepartmentName);

    private sealed record Fact(
        Guid SenderEmployeeId,
        Guid? RecipientEmployeeId,
        KudosType KudosType,
        CompanyValue CompanyValue,
        DateTimeOffset CreatedAt);
}
