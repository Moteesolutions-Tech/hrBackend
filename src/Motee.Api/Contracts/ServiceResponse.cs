namespace Motee.Api.Contracts;

// wallet-service's envelope shape, but the HTTP status still carries meaning —
// failures do not come back 200.
//
// Named by the serializer's own camelCase policy rather than by attributes, so the
// envelope reads the same way as everything inside it. It used to carry explicit
// snake_case names copied from wallet-service, which left one key — response_code —
// spelled differently from the several hundred payload fields around it. A client
// destructuring responseCode and firstName from the same response should not have to
// remember which convention each belongs to, and an attribute per property is a place
// for the next one to be forgotten.
public sealed record ServiceResponse<T>
{
    public required string ResponseCode { get; init; }

    public required bool Success { get; init; }

    public string? Message { get; init; }

    public T? Data { get; init; }

    public static ServiceResponse<T> Ok(T data, string? message = null) => new()
    {
        ResponseCode = MoteeStatusCodes.Success,
        Success = true,
        Message = message,
        Data = data,
    };

    public static ServiceResponse<T> Created(T data, string? message = null) => new()
    {
        ResponseCode = MoteeStatusCodes.Created,
        Success = true,
        Message = message,
        Data = data,
    };

    public static ServiceResponse<T> Failure(string responseCode, string message) => new()
    {
        ResponseCode = responseCode,
        Success = false,
        Message = message,
        Data = default,
    };
}
