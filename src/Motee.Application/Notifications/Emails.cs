using Motee.Domain.Auth;
using Motee.Domain.Employees;

namespace Motee.Application.Notifications;

// Every kind of email the system sends, in one place. A model carries the facts; the
// template beside it decides the words. Reading this file tells you what Motee is
// capable of sending, which was previously only discoverable by grepping for string
// concatenation inside services.

// The verification code itself. Purpose selects the wording - the same code is used to
// confirm an address and to authorise a password reset, and telling someone the wrong
// one is how phishing training gets undone.
public sealed record OtpCodeEmail
{
    public required string Code { get; init; }

    public required TimeSpan Lifetime { get; init; }

    public required OtpPurpose Purpose { get; init; }
}

// Sent when someone tries to register with an address that already has an account.
//
// This exists because the registration endpoint must answer identically whether or not
// the address is known - otherwise anyone can test a list of addresses and learn which
// belong to Motee customers. The response says "check your email" either way, and the
// mailbox is where the two cases diverge, because only the real owner can read it.
//
// It is also a security notice in its own right: if the recipient did not attempt this,
// somebody else is typing their address into sign-up forms.
public sealed record AccountAlreadyExistsEmail
{
    public required string SignInUrl { get; init; }

    public required string ForgotPasswordUrl { get; init; }
}

// A finished CSV export. The link is pre-signed and bypasses the app, so the wording has
// to say plainly that anyone holding it can read the file.
public sealed record ExportReadyEmail
{
    public required int RowCount { get; init; }

    public required string DownloadUrl { get; init; }

    public required TimeSpan LinkLifetime { get; init; }

    public required DateTimeOffset AvailableUntil { get; init; }
}

// The way into the product for someone who has a record but no account. Purpose is the
// difference between "finish your onboarding" and "your employer already did it, just
// pick a password" - the second must not imply there is a form to fill in.
public sealed record EmployeeInviteEmail
{
    public required string JoinUrl { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required InvitationPurpose Purpose { get; init; }
}
