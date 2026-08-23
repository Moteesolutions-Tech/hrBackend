namespace Motee.Application.Auth;

public interface IAccessTokenService
{
    AccessToken Issue(TokenSubject subject);
}

public sealed record AccessToken
{
    public required string Value { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}
