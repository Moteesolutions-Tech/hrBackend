using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Authorization;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// Who can do what, and to whose records. Behind admin.access-levels rather than the
// employees module: editing permissions is a different kind of act from editing
// people, and the person trusted with one is not automatically trusted with the
// other.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/access-levels")]
public class AccessLevelsController(IAccessLevelService accessLevels) : ApiControllerBase
{
    private const string Module = "admin.access-levels";

    [HttpGet]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await accessLevels.ListAsync(cancellationToken));

    // The catalogue the editor renders its matrix from — every module, every action,
    // and which actions imply which. Served rather than duplicated on the client,
    // because a matrix built from a stale list quietly stops offering new modules.
    [HttpGet("catalogue")]
    [RequiresPermission(Module, PermissionAction.View)]
    public IActionResult Catalogue() =>
        Ok(new AccessLevelCatalogue
        {
            Modules = ModuleCatalogue.All,
            Actions = [.. Enum.GetValues<PermissionAction>()],
            ActionDependencies = Enum.GetValues<PermissionAction>()
                .ToDictionary(
                    action => action,
                    action => ActionDependencies.Expand([action])
                        .Where(dependency => dependency != action)
                        .ToArray()),
            ScopeKinds = [.. Enum.GetValues<DataScopeKind>().Where(kind => kind != DataScopeKind.None)],
        });

    [HttpGet("{id:guid}")]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        AccessLevelDto? found = await accessLevels.GetAsync(id, cancellationToken);

        return found is null
            ? Failure<AccessLevelDto>(MoteeStatusCodes.NotFound, "Access level not found.")
            : Ok(found);
    }

    // Created as a draft. Activating it is a second, deliberate act — see the status
    // endpoint below.
    [HttpPost]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        AccessLevelRequest request,
        CancellationToken cancellationToken)
    {
        AccessLevelResult result = await accessLevels.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.AccessLevel!, "Access level created as a draft.")
            : Failure<AccessLevelDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpPut("{id:guid}")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        AccessLevelRequest request,
        CancellationToken cancellationToken)
    {
        AccessLevelResult result = await accessLevels.UpdateAsync(id, request, cancellationToken);

        return result.Succeeded
            ? Ok(result.AccessLevel!, "Access level updated.")
            : Failure<AccessLevelDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    // Activate, or withdraw. Deactivating takes effect at once for everyone holding
    // it, which is what makes it usable when access has to stop now.
    [HttpPost("{id:guid}/status")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> ChangeStatus(
        Guid id,
        ChangeAccessLevelStatusRequest request,
        CancellationToken cancellationToken)
    {
        AccessLevelResult result =
            await accessLevels.ChangeStatusAsync(id, request.Status, cancellationToken);

        return result.Succeeded
            ? Ok(result.AccessLevel!, $"Access level is now {request.Status}.")
            : Failure<AccessLevelDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpDelete("{id:guid}")]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        AccessLevelResult result = await accessLevels.DeleteAsync(id, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Access level deleted.")
            : Failure<object?>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    // Someone can hold several. What they permit is unioned; how far they reach is
    // the narrowest of them.
    [HttpPost("{id:guid}/holders")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Assign(
        Guid id,
        AssignAccessLevelRequest request,
        CancellationToken cancellationToken)
    {
        AccessLevelResult result =
            await accessLevels.AssignAsync(request.UserId, id, cancellationToken);

        return result.Succeeded
            ? Ok(result.AccessLevel!, "Access level assigned.")
            : Failure<AccessLevelDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpDelete("{id:guid}/holders/{userId:guid}")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Withdraw(
        Guid id,
        Guid userId,
        CancellationToken cancellationToken)
    {
        AccessLevelResult result = await accessLevels.WithdrawAsync(userId, id, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Access level withdrawn.")
            : Failure<object?>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    private static string StatusFor(AccessLevelOutcome outcome) => outcome switch
    {
        AccessLevelOutcome.NotFound or AccessLevelOutcome.UserNotFound =>
            MoteeStatusCodes.NotFound,

        // Nothing about the request was malformed; the level is simply not in a state
        // that allows it.
        AccessLevelOutcome.DuplicateName
            or AccessLevelOutcome.NotAssignable
            or AccessLevelOutcome.InUse
            or AccessLevelOutcome.AlreadyHeld
            or AccessLevelOutcome.NotHeld => MoteeStatusCodes.Conflict,

        AccessLevelOutcome.UnknownModule => MoteeStatusCodes.InvalidRequest,

        _ => MoteeStatusCodes.InternalServerError,
    };

    private static string MessageFor(AccessLevelOutcome outcome) => outcome switch
    {
        AccessLevelOutcome.NotFound => "Access level not found.",
        AccessLevelOutcome.UserNotFound => "That user does not exist.",
        AccessLevelOutcome.DuplicateName => "An access level with that name already exists.",
        AccessLevelOutcome.UnknownModule => "That permission names a module we do not have.",
        AccessLevelOutcome.NotAssignable =>
            "Only an active access level can be assigned. Activate it first.",
        AccessLevelOutcome.InUse =>
            "People still hold that access level. Withdraw it from them, or deactivate it instead.",
        AccessLevelOutcome.AlreadyHeld => "They already hold that access level.",
        AccessLevelOutcome.NotHeld => "They do not hold that access level.",
        _ => "Could not complete the request.",
    };
}

public sealed record ChangeAccessLevelStatusRequest
{
    public required AccessLevelStatus Status { get; init; }
}

public sealed record AssignAccessLevelRequest
{
    public required Guid UserId { get; init; }
}

// Everything the permissions editor needs to render itself, from the one place that
// decides it.
public sealed record AccessLevelCatalogue
{
    public required IReadOnlyList<string> Modules { get; init; }

    public required IReadOnlyList<PermissionAction> Actions { get; init; }

    // approve implies view; administer implies view and edit. The editor ticks them
    // for you, and the server expands them again on save regardless.
    public required IReadOnlyDictionary<PermissionAction, PermissionAction[]> ActionDependencies { get; init; }

    public required IReadOnlyList<DataScopeKind> ScopeKinds { get; init; }
}
