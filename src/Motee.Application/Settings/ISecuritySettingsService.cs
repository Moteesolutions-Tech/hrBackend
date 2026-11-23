using Motee.Domain.Settings;

namespace Motee.Application.Settings;

// The security screen's two lists: the devices somebody is signed in on, and
// what has happened on their account.
//
// Both are already in the database. A trusted-devices table would be a list
// that revokes nothing — the device carries on using its refresh token while
// the screen shows it gone — so the list is the live refresh tokens, and
// revoking one signs that device out for real. Login activity is read from the
// audit trail, which already records a successful sign-in and a failed one with
// the IP, the agent and the moment.
public interface ISecuritySettingsService
{
    Task<IReadOnlyList<DeviceSessionDto>> SessionsAsync(
        CancellationToken cancellationToken = default);

    Task<SettingsOutcome> RevokeSessionAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AccountEventDto>> ActivityAsync(
        int take = 25,
        CancellationToken cancellationToken = default);
}

public sealed record DeviceSessionDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Browser { get; init; }

    public required string OperatingSystem { get; init; }

    public string? IpAddress { get; init; }

    public required DateTimeOffset SignedInAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    // The session this request is on. The screen withholds Revoke on it, which
    // is right — signing yourself out from a list of devices reads as a fault
    // rather than an action.
    public required bool IsCurrent { get; init; }
}

public sealed record AccountEventDto
{
    public required Guid Id { get; init; }

    public required AccountEventKind Kind { get; init; }

    public required string Event { get; init; }

    public string? IpAddress { get; init; }

    public string? Device { get; init; }

    public required bool Succeeded { get; init; }

    public required DateTimeOffset At { get; init; }

    // Null, and deliberately. The screen has a Location column filled with
    // "Lagos, Nigeria" from demo data; placing an IP needs a geolocation
    // service this product does not have, and a guessed city on a security
    // screen is the one thing worse than a blank one — somebody decides a
    // sign-in was not theirs on the strength of it.
    public string? Location { get; init; }
}
