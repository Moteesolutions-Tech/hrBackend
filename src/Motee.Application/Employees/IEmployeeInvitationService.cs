using Motee.Application.Auth;
using Motee.Domain.Employees;
using Motee.Domain.Files;
using Motee.Domain.Organisation;

namespace Motee.Application.Employees;

public interface IEmployeeInvitationService
{
    Task<InviteResult> InviteAsync(InviteRequest request, CancellationToken cancellationToken = default);

    // For someone who is already on the payroll but has no way in: everyone added by
    // Manual Entry or Bulk Upload, and anyone whose emailed link expired or went to
    // spam. Issuing revokes any invitation still outstanding, so only the newest link
    // works — otherwise a link from three resends ago is still live.
    Task<IssueInviteResult> IssueAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default);

    // Withdrawn before it was used — someone who left between offer and start date.
    Task<IssueInviteResult> RevokeAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default);

    // Who has been invited and not yet joined, so HR can chase rather than guess.
    Task<IReadOnlyList<PendingInvitationDto>> PendingAsync(
        CancellationToken cancellationToken = default);

    // Anonymous: the joiner has no account yet, so this reaches across tenants by
    // token alone.
    Task<InvitationPreview?> PreviewAsync(string token, CancellationToken cancellationToken = default);

    // The joiner's profile photo, uploaded during self-onboarding — before they have
    // an account, so the token is the only credential. It resolves the tenant, which
    // is what lets the file be stored against the right company at all.
    Task<JoinPhotoResult> UploadPhotoAsync(
        string token,
        JoinPhotoUpload upload,
        CancellationToken cancellationToken = default);

    Task<AcceptInviteResult> AcceptAsync(
        string token,
        AcceptInviteRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record InviteRequest
{
    public required string FirstName { get; init; }

    public string? MiddleName { get; init; }

    public required string LastName { get; init; }

    public required string Email { get; init; }

    public required string JobTitle { get; init; }

    public required Guid DepartmentId { get; init; }

    public required EmploymentType EmploymentType { get; init; }

    public DateOnly? StartDate { get; init; }

    public Guid? ManagerId { get; init; }
}

public sealed record InviteResult
{
    public required EmployeeOutcome Outcome { get; init; }

    public EmployeeDto? Employee { get; init; }

    // Returned so a dev environment can log the link. Never emitted by the API.
    public string? Token { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public bool Succeeded => Outcome == EmployeeOutcome.Succeeded;

    public static InviteResult Failed(EmployeeOutcome outcome) => new() { Outcome = outcome };
}

public enum IssueInviteOutcome
{
    Succeeded,
    EmployeeNotFound,

    // They already have an account, so an invitation would create a second one. They
    // should sign in, or reset their password.
    AccountExists,

    // Revoke found nothing outstanding to withdraw.
    NoInvitationOutstanding,

    // A leaver or a deleted record. Inviting them back in would be a mistake, not a
    // convenience.
    EmployeeNotJoinable,
}

public sealed record IssueInviteResult
{
    public required IssueInviteOutcome Outcome { get; init; }

    // Returned so a dev environment can log the link. Never emitted by the API.
    public string? Token { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public bool Succeeded => Outcome == IssueInviteOutcome.Succeeded;

    public static IssueInviteResult Failed(IssueInviteOutcome outcome) => new() { Outcome = outcome };
}

// The chase list. Deliberately not the raw invitation row — the token hash has no
// business leaving the service.
public sealed record PendingInvitationDto
{
    public required Guid EmployeeId { get; init; }

    public required string Name { get; init; }

    public required string Email { get; init; }

    public string? JobTitle { get; init; }

    public required DateTimeOffset SentAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    // Separate from ExpiresAt so the UI does not have to compare clocks itself, and
    // so "expired" means the same thing here as it does on the join page.
    public required bool Expired { get; init; }
}

// What the join page renders before the person has typed anything.
public sealed record InvitationPreview
{
    public required InvitationOutcome Outcome { get; init; }

    // Which screen to render. Credentials means a password field and nothing else —
    // every other field below is null, because HR already has the record and this
    // person has nothing to complete.
    public InvitationPurpose Purpose { get; init; } = InvitationPurpose.Onboarding;

    public string? FirstName { get; init; }

    public string? MiddleName { get; init; }

    public string? LastName { get; init; }

    public string? Email { get; init; }

    public string? JobTitle { get; init; }

    public Guid? DepartmentId { get; init; }

    public string? Department { get; init; }

    public EmploymentType? EmploymentType { get; init; }

    public DateOnly? StartDate { get; init; }

    public string? CompanyName { get; init; }

    public bool Valid => Outcome == InvitationOutcome.Valid;
}

// Self-onboarding: the same profile the manual wizard collects, about themselves,
// plus the password that creates their account. Everything the admin set at invite
// time is deliberately absent — sending a different departmentId must not be able to
// move someone into another department.
public sealed record AcceptInviteRequest : SelfProfileRequest
{
    public required string Password { get; init; }
}

public sealed record JoinPhotoUpload
{
    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public required Stream Content { get; init; }

    public required long SizeBytes { get; init; }
}

public sealed record JoinPhotoResult
{
    public required InvitationOutcome Outcome { get; init; }

    // Set only when the link was good but the file was not — a screenshot of a PDF
    // renamed to .png, or something over the size limit.
    public FileRejection? Rejection { get; init; }

    public Guid? FileId { get; init; }

    public bool Succeeded => Outcome == InvitationOutcome.Valid && Rejection is null;

    public static JoinPhotoResult Failed(InvitationOutcome outcome) => new() { Outcome = outcome };
}

public enum AcceptInviteOutcome
{
    Succeeded,
    InvalidToken,
    Expired,
    AlreadyUsed,
    Revoked,
    WeakPassword,

    // An account already exists for this address, so there is nothing to create.
    AccountExists,
}

public sealed record AcceptInviteResult
{
    public required AcceptInviteOutcome Outcome { get; init; }

    public IssuedSession? Session { get; init; }

    public bool Succeeded => Outcome == AcceptInviteOutcome.Succeeded;

    public static AcceptInviteResult Failed(AcceptInviteOutcome outcome) => new() { Outcome = outcome };
}
