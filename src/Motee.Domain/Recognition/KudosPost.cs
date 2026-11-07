using Motee.Domain.Common;
using Motee.Domain.Tenants;

namespace Motee.Domain.Recognition;

public enum KudosType
{
    Teamwork,
    Innovation,
    Leadership,
    CustomerFocus,
    Excellence,
    Growth,

    // Needs CustomTypeName, or it is a kudos with no stated reason.
    Custom,
}

public enum CompanyValue
{
    Integrity,
    Innovation,
    Collaboration,
    Excellence,
    CustomerFirst,
}

public enum ReactionType
{
    Heart,
    Fire,
    Clap,
    Star,
    Rocket,
    Celebrate,
}

// One ordered axis rather than the client's two booleans and a flag.
//
// Its type carries `isPublic` and `isPrivate` and sets them from the same bit,
// plus a separate `isBroadcast` that can contradict either — so "a private
// kudos announced to the whole company" is representable there. Here it is not.
public enum KudosVisibility
{
    // The sender, the recipient, and nobody else.
    Private,

    // On the wall.
    Public,

    // On the wall and announced.
    Broadcast,
}

// The client's union is individual | department | team. Its own comment says
// team "reuses the department roster as its source list — there is no separate
// team entity modelled anywhere else in the app", so there are two kinds here
// and `team` is accepted as a spelling of department.
public enum KudosRecipientKind
{
    Individual,
    Department,
}

// A kudos on the recognition wall.
//
// Addressed to one person or to one department, never both: a kudos with both
// has no answer to whose wall it belongs on. Reactions and comments are their
// own rows, so a count is never stored — the client's type carries both a
// `users` list and a `reactedBy` list alongside a `count`, which is three
// places for one fact.
public class KudosPost : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid SenderEmployeeId { get; set; }

    // One of these two, never both and never neither.
    public Guid? RecipientEmployeeId { get; set; }

    public Guid? RecipientDepartmentId { get; set; }

    public KudosType KudosType { get; set; }

    // Only meaningful — and required — when the type is Custom.
    public string? CustomTypeName { get; set; }

    public CompanyValue CompanyValue { get; set; }

    public required string Message { get; set; }

    public KudosVisibility Visibility { get; set; } = KudosVisibility.Public;

    // Held at the top of the wall by HR.
    public bool IsPinned { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// One person's reaction of one kind to one post. A row rather than a counter,
// so "did I react" and "how many reacted" are the same fact asked two ways.
public class KudosReaction : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid KudosPostId { get; set; }

    public Guid EmployeeId { get; set; }

    public ReactionType Type { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public class KudosComment : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid KudosPostId { get; set; }

    public Guid AuthorEmployeeId { get; set; }

    public required string Message { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum KudosRefusal
{
    None,
    MessageRequired,
    MessageTooLong,
    RecipientRequired,

    // Addressed to a person and a department at once.
    RecipientAmbiguous,

    CustomTypeNameRequired,

    // A recognition wall where people congratulate themselves is a wall
    // nobody reads.
    CannotSendToSelf,

    CommentRequired,
    CommentTooLong,

    // Reacting to, or commenting on, something the caller cannot see.
    NotVisible,
}

public static class KudosRecipients
{
    public static bool TryParse(string? value, out KudosRecipientKind kind)
    {
        kind = KudosRecipientKind.Individual;

        switch (value?.Trim().ToLowerInvariant())
        {
            case "individual":
                return true;

            // Two spellings of the same thing, because there is no team entity
            // for the second one to mean anything else.
            case "department":
            case "team":
                kind = KudosRecipientKind.Department;
                return true;

            default:
                return false;
        }
    }
}

public static class KudosVisibilities
{
    public static bool TryParse(string? value, out KudosVisibility visibility)
    {
        visibility = KudosVisibility.Public;

        switch (value?.Trim().ToLowerInvariant())
        {
            case "private":
                visibility = KudosVisibility.Private;
                return true;
            case "public":
                return true;
            case "broadcast":
                visibility = KudosVisibility.Broadcast;
                return true;
            default:
                return false;
        }
    }
}

public static class KudosPosts
{
    // The form's own limits.
    public const int ShortestMessage = 10;

    public const int LongestMessage = 500;

    public const int LongestCustomTypeName = 60;

    public const int LongestComment = 1000;

    public static KudosRefusal Refusal(KudosPost post)
    {
        string message = post.Message?.Trim() ?? string.Empty;

        if (message.Length < ShortestMessage)
        {
            return KudosRefusal.MessageRequired;
        }

        if (message.Length > LongestMessage)
        {
            return KudosRefusal.MessageTooLong;
        }

        if (post.RecipientEmployeeId is null && post.RecipientDepartmentId is null)
        {
            return KudosRefusal.RecipientRequired;
        }

        if (post.RecipientEmployeeId is not null && post.RecipientDepartmentId is not null)
        {
            return KudosRefusal.RecipientAmbiguous;
        }

        if (post.KudosType == KudosType.Custom
            && string.IsNullOrWhiteSpace(post.CustomTypeName))
        {
            return KudosRefusal.CustomTypeNameRequired;
        }

        // Only for an individual kudos. Thanking your own department is
        // ordinary, and the sender is not its recipient in any useful sense.
        return post.RecipientEmployeeId == post.SenderEmployeeId
            ? KudosRefusal.CannotSendToSelf
            : KudosRefusal.None;
    }

    public static KudosRefusal CommentRefusal(string? message)
    {
        string trimmed = message?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return KudosRefusal.CommentRequired;
        }

        return trimmed.Length > LongestComment
            ? KudosRefusal.CommentTooLong
            : KudosRefusal.None;
    }

    public static KudosRecipientKind RecipientKindOf(KudosPost post) =>
        post.RecipientDepartmentId is not null
            ? KudosRecipientKind.Department
            : KudosRecipientKind.Individual;

    // On the wall for everybody, or between the people it concerns. A private
    // department kudos reaches that department, because that is who it was
    // addressed to.
    public static bool CanSee(
        KudosPost post,
        Guid viewerEmployeeId,
        Guid? viewerDepartmentId) =>
        post.Visibility != KudosVisibility.Private
            || post.SenderEmployeeId == viewerEmployeeId
            || post.RecipientEmployeeId == viewerEmployeeId
            || (post.RecipientDepartmentId is Guid department
                && viewerDepartmentId == department);

    public static bool IsAnnounced(KudosPost post) =>
        post.Visibility == KudosVisibility.Broadcast;
}

// One kudos, reduced to what the wall's figures need.
public sealed record KudosFacts
{
    public required Guid SenderEmployeeId { get; init; }

    // Null for a department kudos, which credits nobody's received count —
    // crediting every member would let one post swamp the board.
    public Guid? RecipientEmployeeId { get; init; }

    public required KudosType KudosType { get; init; }

    public required CompanyValue CompanyValue { get; init; }

    public required DateOnly SentOn { get; init; }
}

public sealed record KudosTally
{
    public required int Total { get; init; }

    public required int ThisMonth { get; init; }

    // Null when nothing has been sent.
    public KudosType? CommonestType { get; init; }

    public required int CommonestTypeCount { get; init; }

    // Reported separately from the type because the card is labelled "Most
    // Used Value" while the code behind it counts the type. Both are here so
    // the screen can use whichever it meant.
    public CompanyValue? CommonestValue { get; init; }

    public required int CommonestValueCount { get; init; }

    public required int Givers { get; init; }

    // Over the real headcount. The client divides by a hardcoded 156.
    public required int ParticipationPercent { get; init; }
}

public sealed record KudosStanding
{
    public required Guid EmployeeId { get; init; }

    public required int Rank { get; init; }

    public required int Received { get; init; }

    public required int Sent { get; init; }

    // Consecutive weeks, back from the current one, in which this person gave
    // at least one kudos. The client's leaderboard carries a streak and
    // hardcodes it to zero with no definition anywhere; this is the
    // definition, written down so it can be corrected rather than guessed at.
    public required int Streak { get; init; }
}

public static class KudosWall
{
    public static KudosTally Of(
        IReadOnlyList<KudosFacts> kudos,
        int headcount,
        DateOnly asAt)
    {
        int givers = kudos.Select(row => row.SenderEmployeeId).Distinct().Count();

        (KudosType Key, int Count)? type = kudos
            .GroupBy(row => row.KudosType)
            .Select(group => (Key: group.Key, Count: group.Count()))
            .OrderByDescending(row => row.Count)
            .ThenBy(row => row.Key)
            .Cast<(KudosType Key, int Count)?>()
            .FirstOrDefault();

        (CompanyValue Key, int Count)? value = kudos
            .GroupBy(row => row.CompanyValue)
            .Select(group => (Key: group.Key, Count: group.Count()))
            .OrderByDescending(row => row.Count)
            .ThenBy(row => row.Key)
            .Cast<(CompanyValue Key, int Count)?>()
            .FirstOrDefault();

        return new KudosTally
        {
            Total = kudos.Count,
            ThisMonth = kudos.Count(row =>
                row.SentOn.Year == asAt.Year && row.SentOn.Month == asAt.Month),
            CommonestType = type?.Key,
            CommonestTypeCount = type?.Count ?? 0,
            CommonestValue = value?.Key,
            CommonestValueCount = value?.Count ?? 0,
            Givers = givers,

            // Zero rather than a division by nothing. A company with no
            // employees has no participation to report.
            ParticipationPercent = headcount <= 0
                ? 0
                : (int)Math.Round(givers / (decimal)headcount * 100m, MidpointRounding.AwayFromZero),
        };
    }

    public static IReadOnlyList<KudosStanding> Leaderboard(
        IReadOnlyList<KudosFacts> kudos,
        DateOnly asAt,
        int take)
    {
        Dictionary<Guid, int> received = kudos
            .Where(row => row.RecipientEmployeeId is not null)
            .GroupBy(row => row.RecipientEmployeeId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        Dictionary<Guid, List<DateOnly>> sent = kudos
            .GroupBy(row => row.SenderEmployeeId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.SentOn).ToList());

        // Anybody who has either given or received, which is the client's own
        // filter.
        List<Guid> people = [.. received.Keys.Concat(sent.Keys).Distinct()];

        return
        [
            .. people
                .Select(id => new
                {
                    Id = id,
                    Received = received.GetValueOrDefault(id),
                    Sent = sent.GetValueOrDefault(id, []).Count,
                    Streak = StreakOf(sent.GetValueOrDefault(id, []), asAt),
                })
                .OrderByDescending(row => row.Received)
                .ThenByDescending(row => row.Sent)
                .ThenBy(row => row.Id)
                .Take(take)
                .Select((row, index) => new KudosStanding
                {
                    EmployeeId = row.Id,
                    Rank = index + 1,
                    Received = row.Received,
                    Sent = row.Sent,
                    Streak = row.Streak,
                }),
        ];
    }

    // Consecutive weeks of giving, counted back from the current one. A week
    // still running does not break a run: somebody who gave last week but not
    // yet today is mid-week, not lapsed.
    private static int StreakOf(IReadOnlyList<DateOnly> sentOn, DateOnly asAt)
    {
        if (sentOn.Count == 0)
        {
            return 0;
        }

        HashSet<DateOnly> weeks = [.. sentOn.Select(TenantClock.WeekStart)];

        DateOnly thisWeek = TenantClock.WeekStart(asAt);

        DateOnly cursor = weeks.Contains(thisWeek) ? thisWeek : thisWeek.AddDays(-7);

        int streak = 0;

        while (weeks.Contains(cursor))
        {
            streak++;
            cursor = cursor.AddDays(-7);
        }

        return streak;
    }
}
