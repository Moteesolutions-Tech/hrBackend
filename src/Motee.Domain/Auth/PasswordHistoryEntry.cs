namespace Motee.Domain.Auth;

// A password this account has used before.
//
// Kept only where the company's policy forbids reuse, and trimmed to the depth
// the policy asks for: a company that forbids the last five keeps five. With
// the policy off nothing is written at all, because a table of everybody's old
// password hashes that no rule reads is a liability with no purpose.
//
// What is stored is the Identity hash, the same thing users.password_hash
// holds — never the password. Checking reuse is verifying a candidate against
// each kept hash, which is what Identity's hasher is for.
public class PasswordHistoryEntry
{
    public Guid Id { get; set; }

    public required Guid UserId { get; set; }

    public required string PasswordHash { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }
}
