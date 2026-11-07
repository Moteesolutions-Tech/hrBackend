using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Recognition;
using Motee.Domain.Authorization;
using Motee.Domain.Identity;
using Motee.Domain.Recognition;

namespace Motee.Api.Controllers;

// Kudos — the recognition wall.
//
// Visibility is enforced in the service, not here: a private kudos never
// leaves the database unless the caller is the sender, the recipient, or in
// the department it was addressed to. There is no flag on the wire for a
// client to honour or forget.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/kudos")]
public class KudosController(
    IKudosService kudos,
    IUserPermissions userPermissions) : ApiControllerBase
{
    private const string Module = "workspace.kudos";

    [HttpGet]
    [ProducesResponseType<PagedResult<KudosPostDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Wall(
        [FromQuery] KudosQuery query,
        CancellationToken cancellationToken) =>
        Ok(await kudos.WallAsync(query, cancellationToken));

    [HttpGet("summary")]
    [ProducesResponseType<KudosTally>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        Ok(await kudos.SummaryAsync(cancellationToken));

    [HttpGet("leaderboard")]
    [ProducesResponseType<IReadOnlyList<KudosStandingDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Leaderboard(
        [FromQuery] int take,
        CancellationToken cancellationToken) =>
        Ok(await kudos.LeaderboardAsync(take <= 0 ? 10 : take, cancellationToken));

    // What the signed-in person has received and given. Authorize only:
    // reading your own recognition is not an HR function.
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<KudosForMeDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(await kudos.MineAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<KudosPostDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        KudosPostDto? found = await kudos.GetAsync(id, cancellationToken);

        return found is null
            ? Failure<KudosPostDto>(MoteeStatusCodes.NotFound, "Kudos not found.")
            : Ok(found);
    }

    [HttpPost]
    [ProducesResponseType<KudosPostDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Send(
        KudosRequest request,
        CancellationToken cancellationToken)
    {
        KudosResult result = await kudos.SendAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Post!, "Kudos sent.")
            : Failure<KudosPostDto>(StatusFor(result), MessageFor(result));
    }

    // Whoever wrote it may withdraw it; taking down somebody else's is a
    // moderation decision and needs the module's Delete permission.
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Authorize]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken)
    {
        KudosResult result = await kudos.RemoveAsync(
            id,
            await HasAsync(PermissionAction.Delete, cancellationToken),
            cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Kudos removed.")
            : Failure<object?>(StatusFor(result), MessageFor(result));
    }

    [HttpPost("{id:guid}/pin")]
    [ProducesResponseType<KudosPostDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Pin(Guid id, CancellationToken cancellationToken) =>
        Respond(await kudos.SetPinnedAsync(id, true, cancellationToken), "Kudos pinned.");

    [HttpDelete("{id:guid}/pin")]
    [ProducesResponseType<KudosPostDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Unpin(Guid id, CancellationToken cancellationToken) =>
        Respond(await kudos.SetPinnedAsync(id, false, cancellationToken), "Kudos unpinned.");

    // A toggle: the same reaction twice takes it back, which is what the
    // feed's single handler does.
    [HttpPost("{id:guid}/reactions")]
    [ProducesResponseType<KudosPostDto>(StatusCodes.Status200OK)]
    [Authorize]
    public async Task<IActionResult> React(
        Guid id,
        ReactionRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await kudos.ToggleReactionAsync(id, request.Type, cancellationToken),
            "Reaction saved.");

    [HttpPost("{id:guid}/comments")]
    [ProducesResponseType<KudosPostDto>(StatusCodes.Status201Created)]
    [Authorize]
    public async Task<IActionResult> Comment(
        Guid id,
        KudosCommentRequest request,
        CancellationToken cancellationToken)
    {
        KudosResult result = await kudos.CommentAsync(id, request.Message, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Post!, "Comment added.")
            : Failure<KudosPostDto>(StatusFor(result), MessageFor(result));
    }

    [HttpDelete("{id:guid}/comments/{commentId:guid}")]
    [ProducesResponseType<KudosPostDto>(StatusCodes.Status200OK)]
    [Authorize]
    public async Task<IActionResult> RemoveComment(
        Guid id,
        Guid commentId,
        CancellationToken cancellationToken) =>
        Respond(
            await kudos.RemoveCommentAsync(
                id,
                commentId,
                await HasAsync(PermissionAction.Delete, cancellationToken),
                cancellationToken),
            "Comment removed.");

    private IActionResult Respond(KudosResult result, string message) =>
        result.Succeeded
            ? Ok(result.Post!, message)
            : Failure<KudosPostDto>(StatusFor(result), MessageFor(result));

    // Resolved here rather than as an attribute because one route serves both
    // cases: withdrawing your own kudos needs nothing, and taking down
    // somebody else's needs the moderation grant.
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

    private static string StatusFor(KudosResult result) => result.Outcome switch
    {
        KudosOutcome.NotFound or KudosOutcome.EmployeeNotFound
            or KudosOutcome.DepartmentNotFound => MoteeStatusCodes.NotFound,

        KudosOutcome.Forbidden or KudosOutcome.NoEmployeeRecord => MoteeStatusCodes.Forbidden,

        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(KudosResult result) => result.Outcome switch
    {
        KudosOutcome.NotFound => "Kudos not found.",
        KudosOutcome.EmployeeNotFound => "That person does not work here.",
        KudosOutcome.DepartmentNotFound => "That department does not exist.",
        KudosOutcome.Forbidden => "Only whoever sent this can take it down.",
        KudosOutcome.NoEmployeeRecord =>
            "Your account has no employee record, so it cannot give or receive kudos.",
        _ => ReasonFor(result.Refusal),
    };

    private static string ReasonFor(KudosRefusal refusal) => refusal switch
    {
        KudosRefusal.MessageRequired =>
            $"Say what they did — at least {KudosPosts.ShortestMessage} characters.",
        KudosRefusal.MessageTooLong =>
            $"Keep it to {KudosPosts.LongestMessage} characters or fewer.",
        KudosRefusal.RecipientRequired => "Choose who this is for.",
        KudosRefusal.RecipientAmbiguous =>
            "Send this to a person or to a department, not both.",
        KudosRefusal.CustomTypeNameRequired => "Name the custom kudos type.",
        KudosRefusal.CannotSendToSelf => "Kudos are for recognising somebody else.",
        KudosRefusal.CommentRequired => "Write something before posting.",
        KudosRefusal.CommentTooLong =>
            $"Keep the comment to {KudosPosts.LongestComment} characters or fewer.",
        _ => "Could not send the kudos.",
    };
}

public sealed record ReactionRequest
{
    public required ReactionType Type { get; init; }
}

public sealed record KudosCommentRequest
{
    public required string Message { get; init; }
}
