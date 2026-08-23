using Motee.Domain.Common;

namespace Motee.Domain.Employees;

public enum InvitationOutcome
{
    Valid,
    Expired,

    // Already used. The account exists, so the joiner should sign in instead.
    Consumed,

    // Withdrawn by HR before it was used.
    Revoked,
}

// What the link is for, which decides what the join page shows and asks.
public enum InvitationPurpose
{
    // The Send Invite route: HR entered the minimum, and the person fills in the
    // rest of their own profile. The page shows them what HR already recorded so
    // they are not asked for it twice.
    Onboarding,

    // Manual Entry and Bulk Upload: HR already has the record, so there is nothing
    // for the person to complete and nothing they need to be shown. All that is
    // missing is a password.
    Credentials,
}

public class EmployeeInvitation : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    // Only the hash is stored. The raw token exists once, in the emailed link, so a
    // leaked database cannot be used to complete anyone's onboarding.
    public required string TokenHash { get; set; }

    public required string Email { get; set; }

    public InvitationPurpose Purpose { get; set; } = InvitationPurpose.Onboarding;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid? InvitedByUserId { get; set; }
}

public static class InvitationPolicy
{
    // Long enough to survive a holiday, short enough that a forwarded link does not
    // stay live for months.
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    public static DateTimeOffset ExpiresAt(DateTimeOffset issuedAt) => issuedAt.Add(Lifetime);

    public static InvitationOutcome Evaluate(EmployeeInvitation invitation, DateTimeOffset now)
    {
        // Consumed outranks expired: telling someone their link expired when they
        // already have an account sends them to the wrong screen.
        if (invitation.ConsumedAt.HasValue)
        {
            return InvitationOutcome.Consumed;
        }

        if (invitation.RevokedAt.HasValue)
        {
            return InvitationOutcome.Revoked;
        }

        return invitation.ExpiresAt <= now ? InvitationOutcome.Expired : InvitationOutcome.Valid;
    }
}
