using Microsoft.AspNetCore.Mvc;

namespace Motee.Api.Contracts;

public abstract class ApiControllerBase : ControllerBase
{
    
    protected IActionResult Envelope<T>(ServiceResponse<T> response) =>
        StatusCode(MoteeStatusCodes.ToHttpStatus(response.ResponseCode), response);

    protected IActionResult Ok<T>(T data, string? message = null) =>
        Envelope(ServiceResponse<T>.Ok(data, message));

    protected IActionResult CreatedEnvelope<T>(T data, string? message = null) =>
        Envelope(ServiceResponse<T>.Created(data, message));

    protected IActionResult Failure<T>(string responseCode, string message) =>
        Envelope(ServiceResponse<T>.Failure(responseCode, message));
}
