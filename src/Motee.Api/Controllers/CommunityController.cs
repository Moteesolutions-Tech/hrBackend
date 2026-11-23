using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Community;
using Motee.Domain.Authorization;
using Motee.Domain.Community;
using Motee.Domain.Identity;

namespace Motee.Api.Controllers;

// The community feed.
//
// Posting, liking and commenting need no module grant: a feed only employees
// with an HR permission can write to is not a community. The grant is what
// lets somebody pin a post or take down a colleague's.
//
// There are no routes here for celebrations or the directory. Celebrations are
// birthdays and anniversaries inside a window, which the calendar already
// derives — read GET /calendar and filter to those categories. The directory
// is the employee list, already served by the employees module.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/community")]
public class CommunityController(
    ICommunityService community,
    IUserPermissions userPermissions) : ApiControllerBase
{
    private const string Module = "workspace.community";

    [HttpGet]
    [Authorize]
    [ProducesResponseType<PagedResult<PostDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Feed(
        [FromQuery] FeedQuery query,
        CancellationToken cancellationToken) =>
        Ok(await community.FeedAsync(query, cancellationToken));

    [HttpGet("summary")]
    [ProducesResponseType<FeedTally>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        Ok(await community.SummaryAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize]
    [ProducesResponseType<PostDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        PostDto? found = await community.GetAsync(id, cancellationToken);

        return found is null
            ? Failure<PostDto>(MoteeStatusCodes.NotFound, "Post not found.")
            : Ok(found);
    }

    [HttpPost]
    [Authorize]
    [ProducesResponseType<PostDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Post(
        PostRequest request,
        CancellationToken cancellationToken)
    {
        PostResult result = await community.PostAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Post!, "Posted.")
            : Failure<PostDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPut("{id:guid}")]
    [Authorize]
    [ProducesResponseType<PostDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(
        Guid id,
        PostRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await community.UpdateAsync(
                id, request, await CanModerateAsync(cancellationToken), cancellationToken),
            "Post updated.");

    [HttpDelete("{id:guid}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        PostResult result = await community.DeleteAsync(
            id, await CanModerateAsync(cancellationToken), cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Post removed.")
            : Failure<object?>(StatusFor(result), MessageFor(result));
    }

    [HttpPost("{id:guid}/pin")]
    [ProducesResponseType<PostDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Pin(Guid id, CancellationToken cancellationToken) =>
        Respond(await community.SetPinnedAsync(id, true, cancellationToken), "Post pinned.");

    [HttpDelete("{id:guid}/pin")]
    [ProducesResponseType<PostDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Unpin(Guid id, CancellationToken cancellationToken) =>
        Respond(await community.SetPinnedAsync(id, false, cancellationToken), "Post unpinned.");

    // A toggle: liking twice takes it back.
    [HttpPost("{id:guid}/like")]
    [Authorize]
    [ProducesResponseType<PostDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Like(Guid id, CancellationToken cancellationToken) =>
        Respond(await community.ToggleLikeAsync(id, cancellationToken), "Saved.");

    [HttpPost("{id:guid}/comments")]
    [Authorize]
    [ProducesResponseType<PostDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Comment(
        Guid id,
        PostCommentRequest request,
        CancellationToken cancellationToken)
    {
        PostResult result = await community.CommentAsync(id, request.Message, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Post!, "Comment added.")
            : Failure<PostDto>(StatusFor(result), MessageFor(result));
    }

    [HttpDelete("{id:guid}/comments/{commentId:guid}")]
    [Authorize]
    [ProducesResponseType<PostDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveComment(
        Guid id,
        Guid commentId,
        CancellationToken cancellationToken) =>
        Respond(
            await community.RemoveCommentAsync(
                id, commentId, await CanModerateAsync(cancellationToken), cancellationToken),
            "Comment removed.");

    // Single choice: voting again moves the vote rather than adding one.
    [HttpPost("{id:guid}/poll-votes")]
    [Authorize]
    [ProducesResponseType<PostDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Vote(
        Guid id,
        PollVoteRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await community.VoteAsync(id, request.OptionId, cancellationToken),
            "Vote counted.");

    private IActionResult Respond(PostResult result, string message) =>
        result.Succeeded
            ? Ok(result.Post!, message)
            : Failure<PostDto>(StatusFor(result), MessageFor(result));

    // Resolved here rather than as an attribute because the same routes serve
    // an author editing their own post and somebody moderating the feed.
    private async Task<bool> CanModerateAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirst(MoteeClaimTypes.Subject)?.Value, out Guid userId))
        {
            return false;
        }

        ResolvedPermissions held = await userPermissions.ForAsync(userId, cancellationToken);

        return held.IsOwner
            || AccessDecision.Resolve(held.Permissions, Module, PermissionAction.Edit)
                .Kind != DataScopeKind.None;
    }

    private static string StatusFor(PostResult result) => result.Outcome switch
    {
        PostOutcome.NotFound or PostOutcome.EmployeeNotFound => MoteeStatusCodes.NotFound,
        PostOutcome.Forbidden or PostOutcome.NoEmployeeRecord => MoteeStatusCodes.Forbidden,
        PostOutcome.PollHasVotes => MoteeStatusCodes.Conflict,

        PostOutcome.Refused when result.Refusal
            is PostRefusal.NotYours => MoteeStatusCodes.Forbidden,

        PostOutcome.Refused when result.Refusal
            is PostRefusal.NotAPoll => MoteeStatusCodes.Conflict,

        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(PostResult result) => result.Outcome switch
    {
        PostOutcome.NotFound => "Post not found.",
        PostOutcome.EmployeeNotFound => "That person does not work here.",
        PostOutcome.Forbidden => "Only whoever wrote this can change it.",
        PostOutcome.NoEmployeeRecord =>
            "Your account has no employee record, so it cannot post to the community.",
        PostOutcome.PollHasVotes =>
            "People have already voted, so the options cannot be changed. Post a new poll "
            + "instead.",
        _ => ReasonFor(result.Refusal),
    };

    private static string ReasonFor(PostRefusal refusal) => refusal switch
    {
        PostRefusal.ContentRequired => "Write something before posting.",
        PostRefusal.ContentTooLong =>
            $"Keep the post to {CommunityPosts.LongestContent:N0} characters or fewer.",
        PostRefusal.EventDateRequired => "An event needs a date.",
        PostRefusal.PollQuestionRequired => "A poll needs a question.",
        PostRefusal.PollNeedsTwoOptions =>
            $"A poll needs at least {CommunityPosts.FewestPollOptions} different answers.",
        PostRefusal.TooManyPollOptions =>
            $"Use {CommunityPosts.MostPollOptions} answers at most.",
        PostRefusal.CelebrationPersonRequired => "Say who the celebration is for.",
        PostRefusal.CommentRequired => "Write something before posting.",
        PostRefusal.CommentTooLong =>
            $"Keep the comment to {CommunityPosts.LongestComment:N0} characters or fewer.",
        PostRefusal.NotAPoll => "This post is not a poll.",
        PostRefusal.UnknownOption => "That answer is not on this poll.",
        PostRefusal.NotYours => "Only whoever wrote this can remove it.",
        _ => "Could not save the post.",
    };
}

public sealed record PostCommentRequest
{
    public required string Message { get; init; }
}

public sealed record PollVoteRequest
{
    public required Guid OptionId { get; init; }
}
