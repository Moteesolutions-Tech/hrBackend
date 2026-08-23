namespace Motee.Domain.Auth;

public class RefreshToken
{
    public Guid Id { get; set; }

    public required Guid UserId { get; set; }

    // SHA-256 of the value handed to the client. The raw token is never stored, so a
    // database leak does not hand over live sessions.
    public required string TokenHash { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    public required DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    // Set when this token is rotated. Presenting a token that already has a
    // successor means the value leaked, and the whole chain is revoked.
    public Guid? ReplacedByTokenId { get; set; }

    public string? CreatedByIp { get; set; }
}
