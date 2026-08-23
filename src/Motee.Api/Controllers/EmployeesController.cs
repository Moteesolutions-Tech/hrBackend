using System.Globalization;
using System.Text;
using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Api.Contracts.Employees;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Exports;
using Motee.Domain.Authorization;
using Motee.Domain.Employees;

namespace Motee.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/employees")]
public class EmployeesController(
    IEmployeeService employees,
    IUserPermissions userPermissions,
    IValidator<EmployeeRequest> validator) : ApiControllerBase
{
    private const string Module = "organization.employees";

    private const string MedicalModule = "employee.medical";

    private const string MedicalDenied =
        "You do not have permission to record medical details for an employee.";

    // A guard against a runaway upload, not a business limit. Each row is a separate
    // round trip, so a very large file would hold the request open for minutes.
    private const int MaxImportRows = 1_000;

    [HttpGet]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] EmployeeFilters filters,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = PagedQuery.DefaultPageSize,
        CancellationToken cancellationToken = default) =>
        Ok(await employees.ListAsync(
            Query(filters) with { Page = page, PageSize = pageSize },
            cancellationToken));

    // The stat cards and the tab counts. Same filters as the list, minus the status
    // it is counting, so a card and the rows it drills into show the same number.
    [HttpGet("stats")]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Stats(
        [FromQuery] EmployeeFilters filters,
        CancellationToken cancellationToken) =>
        Ok(await employees.StatsAsync(
            Query(filters with { Status = null }),
            cancellationToken));

    // Queued rather than streamed. A tenant with thousands of people would hold the
    // request open while the whole file is built, and the caller gets nothing until
    // it finishes. The job emails a link when it is done.
    [HttpPost("export")]
    [RequiresPermission(Module, PermissionAction.Export)]
    public async Task<IActionResult> Export(
        [FromServices] IExportService exports,
        CancellationToken cancellationToken,
        [FromBody] ExportEmployeesRequest? body = null)
    {
        // "Export everyone I can see" is a real request, so an absent body is not an
        // error — it is the unfiltered case.
        ExportEmployeesRequest request = body ?? new ExportEmployeesRequest();

        EmployeeQuery query = Query(request.Filters);

        // The requester's scope is captured now and replayed when the job runs, so a
        // queued export can never see more than the person who asked for it could.
        ExportJobDto job = await exports.RequestEmployeeExportAsync(
            new EmployeeExportFilters
            {
                Scope = query.Scope,
                ViewerEmployeeId = query.ViewerEmployeeId,
                Search = query.Search,
                DepartmentId = query.DepartmentId,
                Status = query.Status,
                EmploymentType = query.EmploymentType,
                WorkMode = query.WorkMode,
                StartedFrom = query.StartedFrom,
                StartedTo = query.StartedTo,
                Columns = request.Columns,
            },
            cancellationToken);

        return CreatedEnvelope(job, "Export started. We will email you when it is ready.");
    }

    // So the column picker is built from what the server actually writes, rather than
    // a list the frontend keeps in step by hand.
    [HttpGet("export/columns")]
    [RequiresPermission(Module, PermissionAction.Export)]
    public IActionResult ExportColumns() =>
        Ok(EmployeeExportColumns.All
            .Select(column => new ExportColumnDto { Key = column.Key, Label = column.Header })
            .ToList());

    // The scope is the caller's, never the request's — a filter narrows what they may
    // already see and can never widen it.
    private EmployeeQuery Query(EmployeeFilters filters) => new()
    {
        Scope = GrantedScope(),
        ViewerEmployeeId = ViewerEmployeeId(),
        Search = filters.Search,
        DepartmentId = filters.DepartmentId,
        Status = filters.Status,
        EmploymentType = filters.EmploymentType,
        WorkMode = filters.WorkMode,
        StartedFrom = filters.StartedFrom,
        StartedTo = filters.StartedTo,
    };

    [HttpGet("{id:guid}")]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        // Reached through the same narrowing as the list, so a Team-scoped manager
        // cannot fetch someone outside their team by guessing an id. Unpaged, or a
        // reachable colleague on page 2 would read as not found.
        IReadOnlyList<EmployeeListItemDto> reachable = await employees.ExportAsync(
            new EmployeeQuery { Scope = GrantedScope(), ViewerEmployeeId = ViewerEmployeeId() },
            cancellationToken);

        if (!reachable.Any(employee => employee.Id == id))
        {
            return Failure<EmployeeDto>(MoteeStatusCodes.NotFound, "Employee not found.");
        }

        EmployeeDto? found = await employees.GetAsync(id, cancellationToken);

        return found is null
            ? Failure<EmployeeDto>(MoteeStatusCodes.NotFound, "Employee not found.")
            : Ok(found);
    }

    [HttpPost]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        EmployeeRequest request,
        CancellationToken cancellationToken)
    {
        ValidationResult validation = await validator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return Failure<EmployeeDto>(
                MoteeStatusCodes.InvalidRequest,
                string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }

        if (request.Medical is not null && !await CanWriteMedicalAsync(cancellationToken))
        {
            return Failure<EmployeeDto>(MoteeStatusCodes.Forbidden, MedicalDenied);
        }

        // Manual, because this is the manual endpoint. The caller does not get a say.
        EmployeeResult result = await employees.CreateAsync(
            request, OnboardingMethod.Manual, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Employee!, "Employee created.")
            : Failure<EmployeeDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    // Health data is not part of the employee record, so it has its own endpoint and
    // its own permission. Returning it beside the profile would hand it to every Line
    // Manager who can open one of their reports.
    [HttpGet("{id:guid}/medical")]
    [RequiresPermission(MedicalModule, PermissionAction.View)]
    public async Task<IActionResult> Medical(Guid id, CancellationToken cancellationToken)
    {
        // An employee with nothing recorded is an empty block, not a 404 — the form
        // needs somewhere to write the first time too.
        MedicalDto? medical = await employees.GetMedicalAsync(id, cancellationToken);

        return await employees.GetAsync(id, cancellationToken) is null
            ? Failure<MedicalDto>(MoteeStatusCodes.NotFound, "Employee not found.")
            : Ok(medical ?? new MedicalDto());
    }

    // Behind the row actions: Deactivate, Start Offboarding, Delete, Restore. Kept
    // apart from Update so a status change is a deliberate act, not something that
    // rides along with a form save.
    [HttpPost("{id:guid}/status")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> ChangeStatus(
        Guid id,
        ChangeEmployeeStatusRequest request,
        CancellationToken cancellationToken)
    {
        EmployeeResult result = await employees.ChangeStatusAsync(id, request.Status, cancellationToken);

        return result.Succeeded
            ? Ok(result.Employee!, $"Employee moved to {request.Status}.")
            : Failure<EmployeeDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    // Soft delete: the row moves to the Deleted tab rather than being removed, so
    // history and anyone who reported to them survive.
    [HttpDelete("{id:guid}")]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        EmployeeResult result = await employees.ChangeStatusAsync(
            id, EmployeeStatus.Deleted, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Employee deleted.")
            : Failure<object?>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    // Creates the employee as Pending and emails them a link to finish their own
    // profile. The token is returned only outside Production, so a developer can
    // follow the link without a mailbox.
    [HttpPost("invite")]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Invite(
        InviteRequest request,
        [FromServices] IEmployeeInvitationService invitations,
        [FromServices] IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        InviteResult result = await invitations.InviteAsync(request, cancellationToken);

        if (!result.Succeeded)
        {
            return Failure<object>(StatusFor(result.Outcome), MessageFor(result.Outcome));
        }

        return CreatedEnvelope<object>(
            new
            {
                employee = result.Employee,
                expiresAt = result.ExpiresAt,
                joinToken = environment.IsProduction() ? null : result.Token,
            },
            "Invitation sent.");
    }

    // The way in for everyone added by Manual Entry or Bulk Upload, who have a record
    // but no account — and the Resend Invite row action, which is the same act.
    // Issuing revokes any earlier link, so only the newest one works.
    [HttpPost("{id:guid}/invitation")]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> IssueInvitation(
        Guid id,
        [FromServices] IEmployeeInvitationService invitations,
        [FromServices] IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        IssueInviteResult result = await invitations.IssueAsync(id, cancellationToken);

        return result.Succeeded
            ? Ok<object>(
                new
                {
                    expiresAt = result.ExpiresAt,
                    joinToken = environment.IsProduction() ? null : result.Token,
                },
                "Invitation sent.")
            : Failure<object>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    // Withdrawn before it was used — someone who left between offer and start date.
    [HttpDelete("{id:guid}/invitation")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> RevokeInvitation(
        Guid id,
        [FromServices] IEmployeeInvitationService invitations,
        CancellationToken cancellationToken)
    {
        IssueInviteResult result = await invitations.RevokeAsync(id, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Invitation revoked.")
            : Failure<object?>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    // Who has been invited and not joined. Expired ones stay on the list: they are
    // exactly the people worth chasing.
    [HttpGet("invitations")]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> PendingInvitations(
        [FromServices] IEmployeeInvitationService invitations,
        CancellationToken cancellationToken) =>
        Ok(await invitations.PendingAsync(cancellationToken));

    // The file HR fills in, generated from the same column list the import reads. A
    // template kept by hand on the other side is how bulk upload ended up shipping
    // one where every row failed validation.
    [HttpGet("import/template")]
    [RequiresPermission(Module, PermissionAction.Create)]
    public IActionResult ImportTemplate() =>
        File(
            System.Text.Encoding.UTF8.GetBytes(EmployeeImportColumns.TemplateCsv()),
            "text/csv",
            "employee-import-template.csv");

    // What each column means and whether it is required, so the upload screen can
    // show guidance without keeping its own copy of the list.
    [HttpGet("import/columns")]
    [RequiresPermission(Module, PermissionAction.Create)]
    public IActionResult ImportColumns() => Ok(EmployeeImportColumns.All);

    // The frontend parses the CSV and posts the rows. Valid rows commit; invalid ones
    // come back with their line number so one typo does not reject the whole file.
    [HttpPost("import")]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Import(
        EmployeeImportRequest request,
        [FromServices] IEmployeeImportService import,
        CancellationToken cancellationToken)
    {
        if (request.Rows.Count > MaxImportRows)
        {
            return Failure<EmployeeImportResult>(
                MoteeStatusCodes.InvalidRequest,
                $"Upload at most {MaxImportRows:N0} rows at a time.");
        }

        EmployeeImportResult result = await import.ImportAsync(
            request.Rows, request.SendInvitations, cancellationToken);

        // Always 200: a partial import is a result to read, not a failed request.
        return Ok(
            result,
            $"{result.Imported} imported, {result.Failed} failed, "
            + $"{result.Invited} emailed a link to set a password.");
    }

    [HttpPut("{id:guid}")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        EmployeeRequest request,
        CancellationToken cancellationToken)
    {
        ValidationResult validation = await validator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return Failure<EmployeeDto>(
                MoteeStatusCodes.InvalidRequest,
                string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }

        if (request.Medical is not null && !await CanWriteMedicalAsync(cancellationToken))
        {
            return Failure<EmployeeDto>(MoteeStatusCodes.Forbidden, MedicalDenied);
        }

        EmployeeResult result = await employees.UpdateAsync(id, request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Employee!, "Employee updated.")
            : Failure<EmployeeDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    // The medical block rides inside the employee form, so the endpoint's own
    // permission cannot express it: someone may create employees without being
    // allowed to record health data. Resolved the same way the handler does.
    private async Task<bool> CanWriteMedicalAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirst(MoteeClaimTypes.Subject)?.Value, out Guid userId))
        {
            return false;
        }

        ResolvedPermissions held = await userPermissions.ForAsync(userId, cancellationToken);

        // Same resolution the handler uses, including the owner bypass. Reading the
        // role claim here instead is what silently denied this to everyone once
        // permissions moved into access levels.
        return held.IsOwner
            || AccessDecision.Resolve(held.Permissions, MedicalModule, PermissionAction.Edit)
                .Kind != DataScopeKind.None;
    }

    // Set by PermissionAuthorizationHandler when the check passed. Absent means the
    // endpoint was reached without one, which must not read as unrestricted.
    // The full reach the handler resolved, including the departments or business
    // units a named scope covers. Absent means the endpoint was reached without a
    // check, which must not read as unrestricted.
    private DataScope GrantedScope() =>
        HttpContext.Items[PermissionAuthorizationHandler.DataScopeItemKey] is DataScope scope
            ? scope
            : DataScope.Nothing;

    private Guid? ViewerEmployeeId() =>
        Guid.TryParse(User.FindFirst(MoteeClaimTypes.EmployeeId)?.Value, out Guid id) ? id : null;

    private static string StatusFor(IssueInviteOutcome outcome) => outcome switch
    {
        IssueInviteOutcome.EmployeeNotFound => MoteeStatusCodes.NotFound,

        // Conflict rather than a bad request: nothing about the request was wrong,
        // the record is simply not in a state that can be invited.
        IssueInviteOutcome.AccountExists or IssueInviteOutcome.EmployeeNotJoinable
            or IssueInviteOutcome.NoInvitationOutstanding => MoteeStatusCodes.Conflict,

        _ => MoteeStatusCodes.InternalServerError,
    };

    private static string MessageFor(IssueInviteOutcome outcome) => outcome switch
    {
        IssueInviteOutcome.EmployeeNotFound => "Employee not found.",
        IssueInviteOutcome.AccountExists =>
            "That employee already has an account. They can sign in, or reset their password.",
        IssueInviteOutcome.EmployeeNotJoinable =>
            "That employee is leaving or has left, so they cannot be invited.",
        IssueInviteOutcome.NoInvitationOutstanding =>
            "There is no outstanding invitation for that employee.",
        _ => "Could not complete the request.",
    };

    private static string StatusFor(EmployeeOutcome outcome) => outcome switch
    {
        EmployeeOutcome.NotFound => MoteeStatusCodes.NotFound,
        EmployeeOutcome.DuplicateEmail or EmployeeOutcome.DuplicateEmployeeNumber =>
            MoteeStatusCodes.Conflict,
        EmployeeOutcome.UnknownDepartment or EmployeeOutcome.UnknownManager
            or EmployeeOutcome.ManagerCycle => MoteeStatusCodes.InvalidRequest,
        EmployeeOutcome.InvalidStatusChange => MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InternalServerError,
    };

    private static string MessageFor(EmployeeOutcome outcome) => outcome switch
    {
        EmployeeOutcome.NotFound => "Employee not found.",
        EmployeeOutcome.DuplicateEmail => "An employee with that email already exists.",
        EmployeeOutcome.DuplicateEmployeeNumber => "That employee ID is already in use.",
        EmployeeOutcome.UnknownDepartment => "That department does not exist.",
        EmployeeOutcome.UnknownManager => "That manager does not exist.",
        EmployeeOutcome.ManagerCycle =>
            "That would make someone their own manager, directly or through their reports.",
        EmployeeOutcome.InvalidStatusChange =>
            "That status change is not allowed from where this employee is now.",
        _ => "Could not save the employee.",
    };
}
