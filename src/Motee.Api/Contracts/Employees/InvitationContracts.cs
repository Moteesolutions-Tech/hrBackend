

// Named types for what used to be anonymous objects. An anonymous payload reaches the
// published document as "data": null, which is the one shape a client cannot build
// against.
public sealed record EmployeeInvitedResponse
{
    public Motee.Application.Employees.EmployeeDto? Employee { get; init; }

    // Nullable because a refused invitation has no expiry to report.
    public DateTimeOffset? ExpiresAt { get; init; }

    // Only ever populated outside production, and only when App:Debug is on.
    //
    // The mailbox round trip is what proves the person joining controls the address;
    // returning the raw token replaces that proof with "whoever could call this
    // endpoint". Documented as nullable so nobody builds a flow that depends on it.
    public string? JoinToken { get; init; }
}

public sealed record InvitationIssuedResponse
{
    // Nullable because a refused invitation has no expiry to report.
    public DateTimeOffset? ExpiresAt { get; init; }

    // Debug builds only — see EmployeeInvitedResponse.JoinToken.
    public string? JoinToken { get; init; }
}

