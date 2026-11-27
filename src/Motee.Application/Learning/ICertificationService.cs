using Motee.Application.Common;
using Motee.Domain.Authorization;
using Motee.Domain.Learning;

namespace Motee.Application.Learning;

// The certification register: completion certificates the product issued, and
// external credentials recorded against somebody.
//
// A completion certificate's expiry comes from the course's own validity and
// is not editable — on compliance training the expiry is the record, and a
// hand-entered one is a record somebody can get wrong.
public interface ICertificationService
{
    Task<PagedResult<CertificationDto>> ListAsync(
        CertificationQuery query,
        Viewer viewer,
        CancellationToken cancellationToken = default);

    Task<CertTally> SummaryAsync(
        CertificationQuery query,
        CancellationToken cancellationToken = default);

    // The caller's own, for their profile.
    Task<IReadOnlyList<CertificationDto>> MineAsync(
        CancellationToken cancellationToken = default);

    Task<CertificationResult> RecordAsync(
        CertificationRequest request,
        CancellationToken cancellationToken = default);

    Task<CertificationResult> UpdateAsync(
        Guid id,
        CertificationRequest request,
        CancellationToken cancellationToken = default);

    Task<CertificationResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

public sealed record CertificationResult
{
    public required LearningOutcome Outcome { get; init; }

    public CertificationDto? Certification { get; init; }

    public LearningRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == LearningOutcome.Succeeded;

    public static CertificationResult Failed(LearningOutcome outcome) =>
        new() { Outcome = outcome };

    public static CertificationResult Refused(LearningRefusal refusal) =>
        new() { Outcome = LearningOutcome.Refused, Refusal = refusal };

    public static CertificationResult Ok(CertificationDto certification) =>
        new() { Outcome = LearningOutcome.Succeeded, Certification = certification };

    public static CertificationResult Done() =>
        new() { Outcome = LearningOutcome.Succeeded };
}

public sealed record CertificationQuery : PagedQuery
{
    public Guid? EmployeeId { get; init; }

    public Guid? CourseId { get; init; }

    public Guid? DepartmentId { get; init; }

    public CertificationKind? CertificationKind { get; init; }

    public CertStatus? Status { get; init; }

    public string? Category { get; init; }

    public string? Search { get; init; }
}

public sealed record CertificationDto
{
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    public string? EmployeeName { get; init; }

    public string? DepartmentName { get; init; }

    public Guid? CourseId { get; init; }

    public string? CourseTitle { get; init; }

    public required CertificationKind CertificationKind { get; init; }

    public required string Title { get; init; }

    public string? IssuingBody { get; init; }

    public string? Category { get; init; }

    public string? CredentialId { get; init; }

    public required DateOnly IssuedOn { get; init; }

    public DateOnly? ExpiresOn { get; init; }

    // Derived from the expiry against today, never stored.
    public required CertStatus Status { get; init; }

    public int? DaysToExpiry { get; init; }

    public Guid? StoredFileId { get; init; }

    // A completion certificate's expiry follows its course, so the register
    // can withhold the field rather than offering one that is overwritten.
    public required bool ExpiryIsDerived { get; init; }
}

public sealed record CertificationRequest
{
    public required Guid EmployeeId { get; init; }

    public required string Title { get; init; }

    public string? IssuingBody { get; init; }

    public string? Category { get; init; }

    public string? CredentialId { get; init; }

    public required DateOnly IssuedOn { get; init; }

    public DateOnly? ExpiresOn { get; init; }

    public Guid? StoredFileId { get; init; }
}
