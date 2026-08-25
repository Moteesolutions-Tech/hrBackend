namespace Motee.Application.Auth;

// Takes pre-validated input — RegisterTenantValidator runs at the API boundary.
public interface ITenantRegistrationService
{
    Task<RegisterTenantResult> RegisterAsync(
        RegisterTenantRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record RegisterTenantResult
{
    public required IReadOnlyList<string> Errors { get; init; }

    // The address is already registered, as opposed to the request being malformed.
    public bool IsConflict { get; init; }

    // Nothing was created because the address already has an account, and the owner has
    // been told by email. Succeeded is deliberately true: the caller must respond
    // exactly as it would to a real registration, or the difference in the response is
    // itself the disclosure this whole path exists to avoid.
    //
    // The only thing this flag may change is whether an OTP is issued - never anything
    // the client can observe.
    public bool AlreadyRegistered { get; init; }

    public bool Succeeded => Errors.Count == 0;

    public Guid TenantId { get; init; }

    public string? TenantSlug { get; init; }

    public Guid UserId { get; init; }

    public string? Email { get; init; }

    public string? CountryCode { get; init; }

    public static RegisterTenantResult Failed(params string[] errors) => new() { Errors = errors };

    public static RegisterTenantResult Conflict(string message) =>
        new() { Errors = [message], IsConflict = true };

    public static RegisterTenantResult AlreadyRegisteredTo(string email) =>
        new() { Errors = [], AlreadyRegistered = true, Email = email };
}
