using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Api.Http;
using Motee.Application.Settings;
using Motee.Domain.Authorization;
using Motee.Domain.Settings;

namespace Motee.Api.Controllers;

// The dropdown options a company configures.
//
// Department, Job Title and Location come back from departments, positions and
// branches — the tables that already own them — and refuse writes, carrying the
// screen that does. A second list of departments would be one the employee form
// never reads, so the dialog would look like it worked and change nothing.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/settings/pick-lists")]
public class PickListsController(IPickListService lists) : ApiControllerBase
{
    private const string Module = SettingsController.Module;

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PickListView>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await lists.ListAsync(cancellationToken));

    [HttpGet("{key}")]
    [ProducesResponseType<PickListView>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Find(string key, CancellationToken cancellationToken)
    {
        PickListView? list = await lists.FindAsync(key, cancellationToken);

        return list is null
            ? Failure<PickListView>(MoteeStatusCodes.NotFound, "No such pick list.")
            : Ok(list);
    }

    [HttpPost("{key}/options")]
    [ProducesResponseType<PickListView>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Add(
        string key,
        PickListValueRequest request,
        CancellationToken cancellationToken)
    {
        PickListResult result = await lists.AddAsync(key, request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.List!, "Value added.")
            : Failure<PickListView>(
                SettingsController.StatusFor(result.Outcome), MessageFor(result));
    }

    [HttpPut("{key}/options/{id:guid}")]
    [ProducesResponseType<PickListView>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        string key,
        Guid id,
        PickListValueRequest request,
        CancellationToken cancellationToken) =>
        Respond(await lists.UpdateAsync(key, id, request, cancellationToken), "Value updated.");

    [HttpDelete("{key}/options/{id:guid}")]
    [ProducesResponseType<PickListView>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Remove(
        string key,
        Guid id,
        CancellationToken cancellationToken) =>
        Respond(await lists.RemoveAsync(key, id, cancellationToken), "Value deleted.");

    // Its own route rather than a flag on the update, because the screen has its
    // own action for it and promoting a default is not editing a value.
    [HttpPut("{key}/options/{id:guid}/default")]
    [ProducesResponseType<PickListView>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SetDefault(
        string key,
        Guid id,
        CancellationToken cancellationToken) =>
        Respond(await lists.SetDefaultAsync(key, id, cancellationToken), "Default set.");

    private IActionResult Respond(PickListResult result, string message) =>
        result.Succeeded
            ? Ok(result.List!, message)
            : Failure<PickListView>(
                SettingsController.StatusFor(result.Outcome), MessageFor(result));

    private static string MessageFor(PickListResult result) => result.Outcome switch
    {
        SettingsOutcome.NotFound => "No such pick list or value.",
        _ => result.Refusal switch
        {
            PickListRefusal.ValueRequired => "Give the value a name.",
            PickListRefusal.ValueTooLong =>
                $"A value can be at most {PickListRules.MaximumValueLength} characters.",
            PickListRefusal.DuplicateValue => "That value is already in this list.",

            // Named, so somebody can go and do the thing they were trying to do
            // rather than being told no.
            PickListRefusal.ListNotConfigurable =>
                "This list comes from its own records and is managed where they are.",
            _ => "That change was not accepted.",
        },
    };
}
