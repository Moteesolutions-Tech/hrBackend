using System.Text.Json.Serialization;

namespace Motee.Api.Contracts;

// wallet-service's envelope shape, but the HTTP status still carries meaning —
// failures do not come back 200. Explicit JsonPropertyName keeps the agreed
// snake_case keys regardless of the camelCase policy applied to payloads.
public sealed record ServiceResponse<T>
{
    [JsonPropertyName("response_code")]
    public required string ResponseCode { get; init; }

    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("data")]
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
