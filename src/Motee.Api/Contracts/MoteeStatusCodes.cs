namespace Motee.Api.Contracts;

// Stable business codes the frontend can branch on, decoupled from HTTP. Generic
// ones mirror their HTTP status; domain-specific ones use the 1000 range so a new
// business outcome never collides with a status code.
public static class MoteeStatusCodes
{
    public const string Success = "00";
    public const string Created = "01";

    public const string InvalidRequest = "400";
    public const string Unauthorized = "401";
    public const string Forbidden = "403";
    public const string NotFound = "404";
    public const string Conflict = "409";

    // The thing existed and no longer does — an expired invitation, not a wrong one.
    public const string Gone = "410";

    // The request body is bigger than the endpoint accepts.
    public const string PayloadTooLarge = "413";
    public const string TooManyRequests = "429";
    public const string InternalServerError = "500";

    public const string OtpIncorrect = "1001";
    public const string OtpExpired = "1002";
    public const string OtpLockedOut = "1003";
    public const string OtpAlreadyUsed = "1004";
    public const string EmailNotConfirmed = "1005";

    public static IReadOnlyList<string> All =>
    [
        Success, Created,
        InvalidRequest, Unauthorized, Forbidden, NotFound, Conflict, Gone, PayloadTooLarge,
        TooManyRequests, InternalServerError,
        OtpIncorrect, OtpExpired, OtpLockedOut, OtpAlreadyUsed,
        EmailNotConfirmed,
    ];

    public static int ToHttpStatus(string responseCode) => responseCode switch
    {
        Success => 200,
        Created => 201,
        InvalidRequest => 400,
        Unauthorized => 401,
        Forbidden => 403,
        NotFound => 404,
        Conflict => 409,
        Gone => 410,
        PayloadTooLarge => 413,
        TooManyRequests => 429,

        // An expired code is gone rather than merely wrong.
        OtpExpired => 410,
        OtpIncorrect => 400,
        OtpAlreadyUsed => 409,
        OtpLockedOut => 429,
        EmailNotConfirmed => 403,

        // Never fall through to 200 — an unmapped code is a bug, not a success.
        _ => 500,
    };
}
