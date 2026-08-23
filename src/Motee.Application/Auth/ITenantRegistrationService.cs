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

    public bool Succeeded => Errors.Count == 0;

    public Guid TenantId { get; init; }

    public string? TenantSlug { get; init; }

    public Guid UserId { get; init; }

    public string? Email { get; init; }

    public string? CountryCode { get; init; }

    public static RegisterTenantResult Failed(params string[] errors) => new() { Errors = errors };

    public static RegisterTenantResult Conflict(string message) =>
        new() { Errors = [message], IsConflict = true };
}
