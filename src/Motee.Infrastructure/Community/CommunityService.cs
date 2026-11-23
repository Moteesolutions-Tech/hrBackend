using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Community;
using Motee.Application.Tenancy;
using Motee.Domain.Community;
using Motee.Domain.Employees;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Community;

// The community feed.
//
// Likes, comments and poll votes are rows, so every count is read rather than
// incremented and "have I already" is the same query as "how many". A poll
// vote is single choice: the client keeps a list of voters per option, which
// lets one person appear under two answers and makes the shares add to more
// than a hundred.
internal sealed class CommunityService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentEmployee currentEmployee,
    TimeProvider timeProvider) : ICommunityService
{
    private string? _zone;

    public async Task<PagedResult<PostDto>> FeedAsync(
        FeedQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<CommunityPost> posts = dbContext.CommunityPosts.AsNoTracking();

        if (query.PostType is PostType type)
        {
            posts = posts.Where(post => post.PostType == type);
        }

        if (query.AuthorEmployeeId is Guid authorId)
        {
            posts = posts.Where(post => post.AuthorEmployeeId == authorId);
        }

        if (query.PinnedOnly == true)
        {
            posts = posts.Where(post => post.IsPinned);
        }

        if (query.UpcomingEventsOnly == true)
        {
            DateOnly today = await TodayAsync(cancellationToken);

            posts = posts.Where(post =>
                post.PostType == PostType.Event && post.EventOn >= today);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{Escaped(query.Search.Trim())}%";

            posts = posts.Where(post =>
                EF.Functions.ILike(post.Content, pattern, "\\")
                || (post.PollQuestion != null
                    && EF.Functions.ILike(post.PollQuestion, pattern, "\\")));
        }

        int total = await posts.CountAsync(cancellationToken);

        List<CommunityPost> page = await posts
            .OrderByDescending(post => post.IsPinned)
            .ThenByDescending(post => post.CreatedAt)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<PostDto>
        {
            Items = await DescribeAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<PostDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        CommunityPost? post = await dbContext.CommunityPosts
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        return post is null ? null : (await DescribeAsync([post], cancellationToken))[0];
    }

    public async Task<FeedTally> SummaryAsync(CancellationToken cancellationToken = default)
    {
        List<Counted> rows = await dbContext.CommunityPosts
            .AsNoTracking()
            .Select(post => new Counted(
                post.PostType,
                dbContext.PostReactions.Count(row => row.PostId == post.Id),
                dbContext.PostComments.Count(row => row.PostId == post.Id),
                dbContext.PostPollVotes.Count(row => row.PostId == post.Id)))
            .ToListAsync(cancellationToken);

        int members = await dbContext.Employees
            .CountAsync(
                employee => EmployeeLifecycle.InPost.Contains(employee.Status),
                cancellationToken);

        return CommunityFeed.Of(
            [
                .. rows.Select(row => new PostFacts
                {
                    PostType = row.PostType,
                    Likes = row.Likes,
                    Comments = row.Comments,
                    PollVotes = row.PollVotes,
                }),
            ],
            members);
    }

    public async Task<PostResult> PostAsync(
        PostRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return PostResult.Failed(PostOutcome.NoEmployeeRecord);
        }

        if (request.CelebrationPersonId is Guid personId)
        {
            bool employed = await dbContext.Employees.AnyAsync(
                employee => employee.Id == personId
                    && employee.Status != EmployeeStatus.Deleted,
                cancellationToken);

            if (!employed)
            {
                return PostResult.Failed(PostOutcome.EmployeeNotFound);
            }
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        CommunityPost post = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            AuthorEmployeeId = me,
            Content = string.Empty,
            CreatedAt = now,
        };

        Apply(post, request, now);

        IReadOnlyList<string> options =
            CommunityPosts.NormaliseOptions(request.PollOptions ?? []);

        PostRefusal refusal = CommunityPosts.Refusal(post, options);

        if (refusal != PostRefusal.None)
        {
            return PostResult.Refused(refusal);
        }

        dbContext.CommunityPosts.Add(post);

        if (post.PostType == PostType.Poll)
        {
            dbContext.PostPollOptions.AddRange(Build(post, options));
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return PostResult.Ok((await DescribeAsync([post], cancellationToken))[0]);
    }

    public async Task<PostResult> UpdateAsync(
        Guid id,
        PostRequest request,
        bool canModerate,
        CancellationToken cancellationToken = default)
    {
        (CommunityPost? post, Guid me, PostOutcome outcome) =
            await MineOrRefusedAsync(id, canModerate, cancellationToken);

        if (post is null)
        {
            return PostResult.Failed(outcome);
        }

        IReadOnlyList<string> options =
            CommunityPosts.NormaliseOptions(request.PollOptions ?? []);

        // The votes point at the option rows, so replacing them would strand
        // every vote already cast.
        bool voted = await dbContext.PostPollVotes
            .AnyAsync(vote => vote.PostId == id, cancellationToken);

        List<PostPollOption> existing = await dbContext.PostPollOptions
            .Where(option => option.PostId == id)
            .OrderBy(option => option.Sequence)
            .ToListAsync(cancellationToken);

        bool optionsChanged = !options.SequenceEqual(
            existing.Select(option => option.Label), StringComparer.Ordinal);

        if (voted && optionsChanged)
        {
            return PostResult.Failed(PostOutcome.PollHasVotes);
        }

        Apply(post, request, timeProvider.GetUtcNow());

        PostRefusal refusal = CommunityPosts.Refusal(post, options);

        if (refusal != PostRefusal.None)
        {
            return PostResult.Refused(refusal);
        }

        if (optionsChanged)
        {
            dbContext.PostPollOptions.RemoveRange(existing);

            if (post.PostType == PostType.Poll)
            {
                dbContext.PostPollOptions.AddRange(Build(post, options));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return PostResult.Ok((await DescribeAsync([post], cancellationToken))[0]);
    }

    public async Task<PostResult> DeleteAsync(
        Guid id,
        bool canModerate,
        CancellationToken cancellationToken = default)
    {
        (CommunityPost? post, Guid me, PostOutcome outcome) =
            await MineOrRefusedAsync(id, canModerate, cancellationToken);

        if (post is null)
        {
            return PostResult.Failed(outcome);
        }

        dbContext.CommunityPosts.Remove(post);

        await dbContext.SaveChangesAsync(cancellationToken);

        return PostResult.Done();
    }

    public async Task<PostResult> SetPinnedAsync(
        Guid id,
        bool pinned,
        CancellationToken cancellationToken = default)
    {
        CommunityPost? post = await FindAsync(id, cancellationToken);

        if (post is null)
        {
            return PostResult.Failed(PostOutcome.NotFound);
        }

        post.IsPinned = pinned;
        post.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return PostResult.Ok((await DescribeAsync([post], cancellationToken))[0]);
    }

    public async Task<PostResult> ToggleLikeAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return PostResult.Failed(PostOutcome.NoEmployeeRecord);
        }

        CommunityPost? post = await FindAsync(id, cancellationToken);

        if (post is null)
        {
            return PostResult.Failed(PostOutcome.NotFound);
        }

        PostReaction? existing = await dbContext.PostReactions
            .FirstOrDefaultAsync(
                row => row.PostId == id && row.EmployeeId == me, cancellationToken);

        if (existing is not null)
        {
            dbContext.PostReactions.Remove(existing);
        }
        else
        {
            dbContext.PostReactions.Add(new PostReaction
            {
                Id = Guid.NewGuid(),
                TenantId = post.TenantId,
                PostId = id,
                EmployeeId = me,
                CreatedAt = timeProvider.GetUtcNow(),
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return PostResult.Ok((await DescribeAsync([post], cancellationToken))[0]);
    }

    public async Task<PostResult> CommentAsync(
        Guid id,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return PostResult.Failed(PostOutcome.NoEmployeeRecord);
        }

        CommunityPost? post = await FindAsync(id, cancellationToken);

        if (post is null)
        {
            return PostResult.Failed(PostOutcome.NotFound);
        }

        PostRefusal refusal = CommunityPosts.CommentRefusal(message);

        if (refusal != PostRefusal.None)
        {
            return PostResult.Refused(refusal);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        dbContext.PostComments.Add(new PostComment
        {
            Id = Guid.NewGuid(),
            TenantId = post.TenantId,
            PostId = id,
            AuthorEmployeeId = me,
            Message = message.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return PostResult.Ok((await DescribeAsync([post], cancellationToken))[0]);
    }

    public async Task<PostResult> RemoveCommentAsync(
        Guid id,
        Guid commentId,
        bool canModerate,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return PostResult.Failed(PostOutcome.NoEmployeeRecord);
        }

        CommunityPost? post = await FindAsync(id, cancellationToken);

        if (post is null)
        {
            return PostResult.Failed(PostOutcome.NotFound);
        }

        PostComment? comment = await dbContext.PostComments
            .FirstOrDefaultAsync(
                row => row.Id == commentId && row.PostId == id, cancellationToken);

        if (comment is null)
        {
            return PostResult.Failed(PostOutcome.NotFound);
        }

        if (comment.AuthorEmployeeId != me && !canModerate)
        {
            return PostResult.Refused(PostRefusal.NotYours);
        }

        dbContext.PostComments.Remove(comment);

        await dbContext.SaveChangesAsync(cancellationToken);

        return PostResult.Ok((await DescribeAsync([post], cancellationToken))[0]);
    }

    public async Task<PostResult> VoteAsync(
        Guid id,
        Guid optionId,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return PostResult.Failed(PostOutcome.NoEmployeeRecord);
        }

        CommunityPost? post = await FindAsync(id, cancellationToken);

        if (post is null)
        {
            return PostResult.Failed(PostOutcome.NotFound);
        }

        if (post.PostType != PostType.Poll)
        {
            return PostResult.Refused(PostRefusal.NotAPoll);
        }

        // An option from another poll would otherwise count towards this one.
        bool ours = await dbContext.PostPollOptions
            .AnyAsync(option => option.Id == optionId && option.PostId == id, cancellationToken);

        if (!ours)
        {
            return PostResult.Refused(PostRefusal.UnknownOption);
        }

        PostPollVote? existing = await dbContext.PostPollVotes
            .FirstOrDefaultAsync(
                vote => vote.PostId == id && vote.EmployeeId == me, cancellationToken);

        // Single choice: voting again moves the vote rather than adding one,
        // so the shares always add up to a hundred.
        if (existing is not null)
        {
            existing.OptionId = optionId;
        }
        else
        {
            dbContext.PostPollVotes.Add(new PostPollVote
            {
                Id = Guid.NewGuid(),
                TenantId = post.TenantId,
                PostId = id,
                OptionId = optionId,
                EmployeeId = me,
                CreatedAt = timeProvider.GetUtcNow(),
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return PostResult.Ok((await DescribeAsync([post], cancellationToken))[0]);
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private Task<CommunityPost?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.CommunityPosts.FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

    private async Task<(CommunityPost? Post, Guid Me, PostOutcome Outcome)> MineOrRefusedAsync(
        Guid id,
        bool canModerate,
        CancellationToken cancellationToken)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return (null, Guid.Empty, PostOutcome.NoEmployeeRecord);
        }

        CommunityPost? post = await FindAsync(id, cancellationToken);

        if (post is null)
        {
            return (null, me, PostOutcome.NotFound);
        }

        // Whoever wrote it, or somebody moderating the feed.
        return post.AuthorEmployeeId == me || canModerate
            ? (post, me, PostOutcome.Succeeded)
            : (null, me, PostOutcome.Forbidden);
    }

    private static void Apply(CommunityPost post, PostRequest request, DateTimeOffset now)
    {
        post.PostType = request.PostType;
        post.Content = request.Content?.Trim() ?? string.Empty;
        post.EventOn = request.EventOn;
        post.EventLocation = Blank(request.EventLocation);
        post.PollQuestion = Blank(request.PollQuestion);
        post.CelebrationKind = request.CelebrationKind;
        post.CelebrationPersonId = request.CelebrationPersonId;
        post.CelebrationDetail = Blank(request.CelebrationDetail);
        post.UpdatedAt = now;
    }

    private static List<PostPollOption> Build(
        CommunityPost post,
        IReadOnlyList<string> options)
    {
        int sequence = 0;

        return
        [
            .. options.Select(label => new PostPollOption
            {
                Id = Guid.NewGuid(),
                TenantId = post.TenantId,
                PostId = post.Id,
                Label = label,
                Sequence = sequence++,
            }),
        ];
    }

    private async Task<List<PostDto>> DescribeAsync(
        IReadOnlyList<CommunityPost> posts,
        CancellationToken cancellationToken)
    {
        if (posts.Count == 0)
        {
            return [];
        }

        Guid me = await currentEmployee.IdAsync(cancellationToken) ?? Guid.Empty;

        List<Guid> ids = [.. posts.Select(post => post.Id)];

        List<PostReaction> likes = await dbContext.PostReactions
            .AsNoTracking()
            .Where(row => ids.Contains(row.PostId))
            .ToListAsync(cancellationToken);

        List<PostComment> comments = await dbContext.PostComments
            .AsNoTracking()
            .Where(row => ids.Contains(row.PostId))
            .OrderBy(row => row.CreatedAt)
            .ToListAsync(cancellationToken);

        List<PostPollOption> options = await dbContext.PostPollOptions
            .AsNoTracking()
            .Where(row => ids.Contains(row.PostId))
            .OrderBy(row => row.Sequence)
            .ToListAsync(cancellationToken);

        List<PostPollVote> votes = await dbContext.PostPollVotes
            .AsNoTracking()
            .Where(row => ids.Contains(row.PostId))
            .ToListAsync(cancellationToken);

        List<Guid> employeeIds =
        [
            .. posts.Select(post => post.AuthorEmployeeId)
                .Concat(posts.Where(post => post.CelebrationPersonId is not null)
                    .Select(post => post.CelebrationPersonId!.Value))
                .Concat(comments.Select(comment => comment.AuthorEmployeeId))
                .Distinct(),
        ];

        Dictionary<Guid, Person> people = await PeopleAsync(employeeIds, cancellationToken);

        return
        [
            .. posts.Select(post =>
            {
                List<PostPollOption> own =
                    [.. options.Where(option => option.PostId == post.Id)];

                List<PostPollVote> cast = [.. votes.Where(vote => vote.PostId == post.Id)];

                IReadOnlyList<PollResult> results = CommunityPosts.PollResults(
                    [
                        .. own.Select(option => new PollTally
                        {
                            Label = option.Label,
                            Votes = cast.Count(vote => vote.OptionId == option.Id),
                        }),
                    ]);

                return new PostDto
                {
                    Id = post.Id,
                    PostType = post.PostType,
                    AuthorEmployeeId = post.AuthorEmployeeId,
                    AuthorName = people.GetValueOrDefault(post.AuthorEmployeeId)?.Name,
                    AuthorDepartmentName =
                        people.GetValueOrDefault(post.AuthorEmployeeId)?.DepartmentName,
                    Content = post.Content,
                    IsPinned = post.IsPinned,
                    EventOn = post.EventOn,
                    EventLocation = post.EventLocation,
                    PollQuestion = post.PollQuestion,
                    PollOptions =
                    [
                        .. own.Select((option, index) => new PollOptionDto
                        {
                            Id = option.Id,
                            Label = option.Label,
                            Votes = results[index].Votes,
                            Share = results[index].Share,
                            Sequence = option.Sequence,
                        }),
                    ],
                    PollVotes = cast.Count,
                    MyVoteOptionId = cast.Find(vote => vote.EmployeeId == me)?.OptionId,
                    CelebrationKind = post.CelebrationKind,
                    CelebrationPersonId = post.CelebrationPersonId,
                    CelebrationPersonName = post.CelebrationPersonId is Guid personId
                        ? people.GetValueOrDefault(personId)?.Name
                        : null,
                    CelebrationDetail = post.CelebrationDetail,
                    Likes = likes.Count(like => like.PostId == post.Id),
                    HasMyLike = likes.Exists(
                        like => like.PostId == post.Id && like.EmployeeId == me),
                    Comments =
                    [
                        .. comments
                            .Where(comment => comment.PostId == post.Id)
                            .Select(comment => new PostCommentDto
                            {
                                Id = comment.Id,
                                AuthorEmployeeId = comment.AuthorEmployeeId,
                                AuthorName =
                                    people.GetValueOrDefault(comment.AuthorEmployeeId)?.Name,
                                AuthorDepartmentName = people
                                    .GetValueOrDefault(comment.AuthorEmployeeId)?.DepartmentName,
                                Message = comment.Message,
                                CanRemove = comment.AuthorEmployeeId == me,
                                CreatedAt = comment.CreatedAt,
                            }),
                    ],
                    CanManage = post.AuthorEmployeeId == me,
                    CreatedAt = post.CreatedAt,
                    UpdatedAt = post.UpdatedAt,
                };
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

    private sealed record Person(string Name, string? DepartmentName);

    private sealed record Counted(PostType PostType, int Likes, int Comments, int PollVotes);
}
