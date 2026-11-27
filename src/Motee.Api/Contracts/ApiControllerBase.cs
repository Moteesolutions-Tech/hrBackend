using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Application.Auth;
using Motee.Domain.Authorization;

namespace Motee.Api.Contracts;

public abstract class ApiControllerBase : ControllerBase
{
    // Who is asking and how much of a register they may read, taken from what the
    // permission handler resolved for the module on this endpoint.
    //
    // Here rather than copied into each controller because every organisation-wide list
    // needs it and the ones that did not have it were reading whole companies. Passing
    // the permission check is not the whole answer — a department-scoped grant still has
    // to narrow its query, and this is what it narrows by.
    protected Viewer Caller() => Viewer.Of(GrantedScope(), ViewerEmployeeId());

    // The full reach the handler resolved, including the departments or business units a
    // named scope covers. Absent means the endpoint was reached without a check, which
    // must not read as unrestricted.
    protected DataScope GrantedScope() =>
        HttpContext.Items[PermissionAuthorizationHandler.DataScopeItemKey] is DataScope scope
            ? scope
            : DataScope.Nothing;

    // The claim, not a lookup: it is already in the token.
    protected Guid? ViewerEmployeeId() =>
        Guid.TryParse(User.FindFirst(MoteeClaimTypes.EmployeeId)?.Value, out Guid id) ? id : null;

    protected IActionResult Envelope<T>(ServiceResponse<T> response) =>
        StatusCode(MoteeStatusCodes.ToHttpStatus(response.ResponseCode), response);

    protected IActionResult Ok<T>(T data, string? message = null) =>
        Envelope(ServiceResponse<T>.Ok(data, message));

    protected IActionResult CreatedEnvelope<T>(T data, string? message = null) =>
        Envelope(ServiceResponse<T>.Created(data, message));

    protected IActionResult Failure<T>(string responseCode, string message) =>
        Envelope(ServiceResponse<T>.Failure(responseCode, message));
}
