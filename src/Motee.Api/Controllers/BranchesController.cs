using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Organisation;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/branches")]
public class BranchesController(IBranchService branches) : ApiControllerBase
{
    // Sites sit under the structure module alongside departments and business units:
    // they are the third axis of the same org chart, configured by the same people.
    private const string Module = "organization.structure";

    [HttpGet]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await branches.ListAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        BranchDto? branch = await branches.GetAsync(id, cancellationToken);

        return branch is null
            ? Failure<BranchDto>(MoteeStatusCodes.NotFound, "Branch not found.")
            : Ok(branch);
    }

    [HttpPost]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        BranchRequest request,
        CancellationToken cancellationToken) =>
        Respond(await branches.CreateAsync(request, cancellationToken), created: true);

    [HttpPut("{id:guid}")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        BranchRequest request,
        CancellationToken cancellationToken) =>
        Respond(await branches.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        BranchOutcome outcome = await branches.DeleteAsync(id, cancellationToken);

        return outcome == BranchOutcome.Succeeded
            ? Ok<object?>(null, "Branch removed.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    private IActionResult Respond(BranchResult result, bool created = false)
    {
        if (!result.Succeeded)
        {
            return Failure<BranchDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
        }

        return created
            ? CreatedEnvelope(result.Branch!, "Branch created.")
            : Ok(result.Branch!, "Branch updated.");
    }

    private static string StatusFor(BranchOutcome outcome) => outcome switch
    {
        BranchOutcome.NotFound => MoteeStatusCodes.NotFound,
        BranchOutcome.DuplicateCode or BranchOutcome.InUse => MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(BranchOutcome outcome) => outcome switch
    {
        BranchOutcome.NotFound => "Branch not found.",
        BranchOutcome.DuplicateCode => "Another site already uses that code.",

        // Says what to do instead, because the alternative is not obvious and the screen
        // offers a reassignment shortcut off the back of it.
        BranchOutcome.InUse =>
            "People are still posted to this site. Move them to another branch first, "
            + "or deactivate this one instead.",
        BranchOutcome.UnknownManager => "That person is not an employee of this company.",
        _ => "Could not save the branch.",
    };
}
