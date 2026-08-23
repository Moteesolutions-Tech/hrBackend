namespace Motee.Application.Auth;

// Mirrors the sign-up form: name parts, work email, company name, password, and
// the country toggle. Password confirmation is checked in the browser and is not
// part of the API contract.
public sealed record RegisterTenantRequest
{
    public required string FirstName { get; init; }

    public string? MiddleName { get; init; }

    public required string LastName { get; init; }

    public required string Email { get; init; }

    public required string CompanyName { get; init; }

    public required string Password { get; init; }

    public required string CountryCode { get; init; }
}
