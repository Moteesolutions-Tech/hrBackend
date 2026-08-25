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
