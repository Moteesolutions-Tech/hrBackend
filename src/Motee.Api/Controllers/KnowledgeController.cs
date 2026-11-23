using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Auth;
using Motee.Application.Knowledge;
using Motee.Domain.Authorization;
using Motee.Domain.Identity;
using Motee.Domain.Knowledge;

namespace Motee.Api.Controllers;

// The knowledge base.
//
// Reading is near-universal here — workspace.knowledge is granted to almost
// every role and to the self-service bundle — so the line that matters is not
// who may read but what. A draft is not an article yet and an archived one has
// been withdrawn; neither leaves the service for somebody without the
// authoring grant, which is resolved below and passed into the service rather
// than left to the client to filter.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/knowledge")]
public class KnowledgeController(
    IKnowledgeService knowledge,
    IUserPermissions userPermissions) : ApiControllerBase
{
    private const string Module = "workspace.knowledge";

    // The authoring list. Without the Edit grant this returns exactly what a
    // reader would see, so a missing permission narrows the list rather than
    // failing the call.
    [HttpGet]
    [ProducesResponseType<KnowledgeListDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] KnowledgeQuery query,
        CancellationToken cancellationToken) =>
        Ok(await knowledge.ListAsync(
            query, await CanAuthorAsync(cancellationToken), cancellationToken));

    // What the company can read, with the per-category counts the employee
    // tiles are built from.
    [HttpGet("published")]
    [Authorize]
    [ProducesResponseType<KnowledgeListDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Published(
        [FromQuery] KnowledgeQuery query,
        CancellationToken cancellationToken) =>
        Ok(await knowledge.PublishedAsync(query, cancellationToken));

    [HttpGet("summary")]
    [ProducesResponseType<KnowledgeTally>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        Ok(await knowledge.SummaryAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize]
    [ProducesResponseType<KnowledgeArticleDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        KnowledgeArticleDto? found = await knowledge.GetAsync(
            id, await CanAuthorAsync(cancellationToken), cancellationToken);

        return found is null
            ? Failure<KnowledgeArticleDto>(MoteeStatusCodes.NotFound, "Article not found.")
            : Ok(found);
    }

    [HttpPost]
    [ProducesResponseType<KnowledgeArticleDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        KnowledgeArticleRequest request,
        CancellationToken cancellationToken)
    {
        KnowledgeResult result = await knowledge.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Article!, "Article saved.")
            : Failure<KnowledgeArticleDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<KnowledgeArticleDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        KnowledgeArticleRequest request,
        CancellationToken cancellationToken) =>
        Respond(await knowledge.UpdateAsync(id, request, cancellationToken), "Article saved.");

    [HttpPost("{id:guid}/status")]
    [ProducesResponseType<KnowledgeArticleDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SetStatus(
        Guid id,
        ArticleStatusRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await knowledge.SetStatusAsync(id, request.Status, cancellationToken),
            request.Status switch
            {
                ArticleStatus.Published => "Article published.",
                ArticleStatus.Archived => "Article archived.",
                _ => "Article moved back to draft.",
            });

    [HttpPost("{id:guid}/feature")]
    [ProducesResponseType<KnowledgeArticleDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Feature(Guid id, CancellationToken cancellationToken) =>
        Respond(
            await knowledge.SetFeaturedAsync(id, true, cancellationToken),
            "Article featured.");

    [HttpDelete("{id:guid}/feature")]
    [ProducesResponseType<KnowledgeArticleDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Unfeature(Guid id, CancellationToken cancellationToken) =>
        Respond(
            await knowledge.SetFeaturedAsync(id, false, cancellationToken),
            "Article unfeatured.");

    // Records that the caller opened it. Refused on anything unpublished,
    // which also stops an author inflating their own figures before the
    // article is out.
    [HttpPost("{id:guid}/read")]
    [Authorize]
    [ProducesResponseType<KnowledgeArticleDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Read(Guid id, CancellationToken cancellationToken) =>
        Respond(await knowledge.RecordReadAsync(id, cancellationToken), "Read recorded.");

    [HttpPost("{id:guid}/feedback")]
    [Authorize]
    [ProducesResponseType<KnowledgeArticleDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Vote(
        Guid id,
        ArticleVoteRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await knowledge.VoteAsync(id, request.Vote, cancellationToken),
            "Thanks — that helps us keep this useful.");

    [HttpDelete("{id:guid}/feedback")]
    [Authorize]
    [ProducesResponseType<KnowledgeArticleDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClearVote(Guid id, CancellationToken cancellationToken) =>
        Respond(await knowledge.ClearVoteAsync(id, cancellationToken), "Feedback withdrawn.");

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        KnowledgeResult result = await knowledge.DeleteAsync(id, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Article deleted.")
            : Failure<object?>(StatusFor(result), MessageFor(result));
    }

    private IActionResult Respond(KnowledgeResult result, string message) =>
        result.Succeeded
            ? Ok(result.Article!, message)
            : Failure<KnowledgeArticleDto>(StatusFor(result), MessageFor(result));

    // Whether this caller may see and write unpublished articles. Resolved
    // here rather than as an attribute because the same routes serve readers
    // and authors, and the difference is what comes back rather than whether
    // the call is allowed.
    private async Task<bool> CanAuthorAsync(CancellationToken cancellationToken)
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

    private static string StatusFor(KnowledgeResult result) => result.Outcome switch
    {
        KnowledgeOutcome.NotFound => MoteeStatusCodes.NotFound,
        KnowledgeOutcome.NoEmployeeRecord => MoteeStatusCodes.Forbidden,

        KnowledgeOutcome.Refused when result.Refusal == KnowledgeRefusal.NotReadable =>
            MoteeStatusCodes.Conflict,

        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(KnowledgeResult result) => result.Outcome switch
    {
        KnowledgeOutcome.NotFound => "Article not found.",
        KnowledgeOutcome.NoEmployeeRecord =>
            "Your account has no employee record, so it cannot write or rate articles.",
        _ => ReasonFor(result.Refusal),
    };

    private static string ReasonFor(KnowledgeRefusal refusal) => refusal switch
    {
        KnowledgeRefusal.TitleRequired =>
            $"Give the article a title of at least {KnowledgeArticles.ShortestTitle} characters.",
        KnowledgeRefusal.TitleTooLong =>
            $"Keep the title to {KnowledgeArticles.LongestTitle} characters or fewer.",
        KnowledgeRefusal.BodyRequired =>
            $"An article needs at least {KnowledgeArticles.ShortestBody} characters of body.",
        KnowledgeRefusal.BodyTooLong =>
            $"Keep the body to {KnowledgeArticles.LongestBody:N0} characters or fewer.",
        KnowledgeRefusal.SummaryTooLong =>
            $"Keep the summary to {KnowledgeArticles.LongestSummary} characters or fewer.",
        KnowledgeRefusal.TooManyTags =>
            $"Use {KnowledgeArticles.MostTags} tags at most — more than that and nobody "
            + "reads them.",
        KnowledgeRefusal.NotReadable =>
            "This article is not published, so it cannot be read or rated yet.",
        _ => "Could not save the article.",
    };
}

public sealed record ArticleStatusRequest
{
    public required ArticleStatus Status { get; init; }
}

public sealed record ArticleVoteRequest
{
    public required ArticleVote Vote { get; init; }
}
