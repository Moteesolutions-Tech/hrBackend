using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Suggestions;
using Motee.Domain.Authorization;
using Motee.Domain.Identity;
using Motee.Domain.Suggestions;

namespace Motee.Api.Controllers;

// The suggestion box.
//
// An anonymous suggestion's submitter is never in the response — for anybody,
// including whoever holds this module. There is no flag here asking the client
// not to render a name, because there is no name in the payload to render.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/suggestions")]
public class SuggestionsController(
    ISuggestionService suggestions,
    IUserPermissions userPermissions) : ApiControllerBase
{
    private const string Module = "workspace.suggestions";

    [HttpGet]
    [ProducesResponseType<PagedResult<SuggestionDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] SuggestionQuery query,
        CancellationToken cancellationToken) =>
        Ok(await suggestions.ListAsync(query, cancellationToken));

    [HttpGet("summary")]
    [ProducesResponseType<SuggestionTally>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        Ok(await suggestions.SummaryAsync(cancellationToken));

    // The caller's own submissions, anonymous ones included. Authorize only:
    // putting an idea in the box and following it is not an HR function, and
    // the module's own grant is narrower than self-service.
    [HttpGet("mine")]
    [Authorize]
    [ProducesResponseType<IReadOnlyList<SuggestionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(await suggestions.MineAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<SuggestionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        SuggestionDto? found = await suggestions.GetAsync(id, cancellationToken);

        return found is null
            ? Failure<SuggestionDto>(MoteeStatusCodes.NotFound, "Suggestion not found.")
            : Ok(found);
    }

    [HttpPost]
    [Authorize]
    [ProducesResponseType<SuggestionDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Submit(
        SuggestionRequest request,
        CancellationToken cancellationToken)
    {
        SuggestionResult result = await suggestions.SubmitAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(
                result.Suggestion!,
                request.IsAnonymous
                    ? "Suggestion submitted anonymously. Your name is not recorded against it "
                        + "anywhere anybody can read."
                    : "Suggestion submitted.")
            : Failure<SuggestionDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPut("{id:guid}/status")]
    [ProducesResponseType<SuggestionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SetStatus(
        Guid id,
        SuggestionStatusRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await suggestions.SetStatusAsync(id, request.Status, cancellationToken),
            "Status updated.");

    [HttpPut("{id:guid}/priority")]
    [ProducesResponseType<SuggestionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SetPriority(
        Guid id,
        SuggestionPriorityRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await suggestions.SetPriorityAsync(id, request.Priority, cancellationToken),
            "Priority updated.");

    [HttpPut("{id:guid}/response")]
    [ProducesResponseType<SuggestionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Respond(
        Guid id,
        SuggestionResponseRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await suggestions.RespondAsync(id, request, cancellationToken),
            "Response saved.");

    [HttpPost("{id:guid}/feature")]
    [ProducesResponseType<SuggestionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Feature(Guid id, CancellationToken cancellationToken) =>
        Respond(await suggestions.SetFeaturedAsync(id, true, cancellationToken),
            "Suggestion featured.");

    [HttpDelete("{id:guid}/feature")]
    [ProducesResponseType<SuggestionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Unfeature(Guid id, CancellationToken cancellationToken) =>
        Respond(await suggestions.SetFeaturedAsync(id, false, cancellationToken),
            "Suggestion unfeatured.");

    // A toggle: voting twice takes the vote back, which is what the board's
    // single handler does.
    [HttpPost("{id:guid}/upvote")]
    [Authorize]
    [ProducesResponseType<SuggestionDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Upvote(Guid id, CancellationToken cancellationToken) =>
        Respond(await suggestions.ToggleUpvoteAsync(id, cancellationToken), "Vote saved.");

    [HttpPost("{id:guid}/comments")]
    [Authorize]
    [ProducesResponseType<SuggestionDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Comment(
        Guid id,
        SuggestionCommentRequest request,
        CancellationToken cancellationToken)
    {
        // Badged as HR's answer where the caller actually holds the module,
        // and frozen onto the row from there.
        bool official = await HasAsync(PermissionAction.Edit, cancellationToken);

        SuggestionResult result = await suggestions.CommentAsync(
            id, request.Message, official, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Suggestion!, "Comment added.")
            : Failure<SuggestionDto>(StatusFor(result), MessageFor(result));
    }

    [HttpDelete("{id:guid}/comments/{commentId:guid}")]
    [Authorize]
    [ProducesResponseType<SuggestionDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveComment(
        Guid id,
        Guid commentId,
        CancellationToken cancellationToken) =>
        Respond(
            await suggestions.RemoveCommentAsync(
                id,
                commentId,
                await HasAsync(PermissionAction.Delete, cancellationToken),
                cancellationToken),
            "Comment removed.");

    private IActionResult Respond(SuggestionResult result, string message) =>
        result.Succeeded
            ? Ok(result.Suggestion!, message)
            : Failure<SuggestionDto>(StatusFor(result), MessageFor(result));

    // Resolved here rather than as an attribute because one route serves both
    // cases: a colleague commenting needs nothing, and HR commenting earns the
    // official badge.
    private async Task<bool> HasAsync(
        PermissionAction action,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirst(MoteeClaimTypes.Subject)?.Value, out Guid userId))
        {
            return false;
        }

        ResolvedPermissions held = await userPermissions.ForAsync(userId, cancellationToken);

        return held.IsOwner
            || AccessDecision.Resolve(held.Permissions, Module, action).Kind != DataScopeKind.None;
    }

    private static string StatusFor(SuggestionResult result) => result.Outcome switch
    {
        SuggestionOutcome.NotFound => MoteeStatusCodes.NotFound,
        SuggestionOutcome.Forbidden or SuggestionOutcome.NoEmployeeRecord =>
            MoteeStatusCodes.Forbidden,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(SuggestionResult result) => result.Outcome switch
    {
        SuggestionOutcome.NotFound => "Suggestion not found.",
        SuggestionOutcome.Forbidden => "Only whoever wrote this can remove it.",
        SuggestionOutcome.NoEmployeeRecord =>
            "Your account has no employee record, so it cannot submit or vote on suggestions.",
        _ => ReasonFor(result.Refusal),
    };

    private static string ReasonFor(SuggestionRefusal refusal) => refusal switch
    {
        SuggestionRefusal.TitleRequired =>
            $"Give the suggestion a title of at least {SuggestionRules.ShortestTitle} characters.",
        SuggestionRefusal.TitleTooLong =>
            $"Keep the title to {SuggestionRules.LongestTitle} characters or fewer.",
        SuggestionRefusal.DescriptionRequired =>
            $"Explain the suggestion in at least {SuggestionRules.ShortestDescription} characters.",
        SuggestionRefusal.DescriptionTooLong =>
            $"Keep the description to {SuggestionRules.LongestDescription} characters or fewer.",
        SuggestionRefusal.AdminResponseTooLong or SuggestionRefusal.ImplementationNotesTooLong =>
            $"Keep it to {SuggestionRules.LongestResponse} characters or fewer.",
        SuggestionRefusal.CommentRequired => "Write something before posting.",
        SuggestionRefusal.CommentTooLong =>
            $"Keep the comment to {SuggestionRules.LongestComment} characters or fewer.",
        _ => "Could not save the suggestion.",
    };
}

public sealed record SuggestionStatusRequest
{
    public required SuggestionStatus Status { get; init; }
}

public sealed record SuggestionPriorityRequest
{
    public required SuggestionPriority Priority { get; init; }
}

public sealed record SuggestionCommentRequest
{
    public required string Message { get; init; }
}
