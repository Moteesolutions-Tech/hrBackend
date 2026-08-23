using Motee.Application.Assets;
using Motee.Application.Common;
using Motee.Domain.Authorization;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;

namespace Motee.Application.Employees;

public interface IEmployeeService
{
    // The caller's scope narrows the rows: All returns the tenant, Department their
    // department, Team their direct reports, Self only themselves.
    Task<PagedResult<EmployeeListItemDto>> ListAsync(
        EmployeeQuery query,
        CancellationToken cancellationToken = default);

    // Same filters and the same scope narrowing, without paging: an export covers
    // what the user is looking at, not the page they happen to be on.
    Task<IReadOnlyList<EmployeeListItemDto>> ExportAsync(
        EmployeeQuery query,
        CancellationToken cancellationToken = default);

    // Counts behind the stat cards and the tab strip. Same filters and the same scope
    // narrowing as the list, so a card and the rows it drills into agree. Status is
    // ignored — it is what is being counted.
    Task<EmployeeStatsDto> StatsAsync(
        EmployeeQuery query,
        CancellationToken cancellationToken = default);

    Task<EmployeeDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // Health data, so it is never part of the employee record. The endpoint that
    // exposes it sits behind employee.medical, which only SuperAdmin and HrAdmin hold.
    Task<MedicalDto?> GetMedicalAsync(Guid employeeId, CancellationToken cancellationToken = default);

    // How this person got here is the endpoint's fact, not the caller's. Accepting it
    // in the request let a client claim to be the invite route and corrupt the one
    // field that tells the three onboarding paths apart.
    Task<EmployeeResult> CreateAsync(
        EmployeeRequest request,
        OnboardingMethod via = OnboardingMethod.Manual,
        CancellationToken cancellationToken = default);

    Task<EmployeeResult> UpdateAsync(Guid id, EmployeeRequest request, CancellationToken cancellationToken = default);

    // Self-onboarding. Writes only what the person may say about themselves and
    // leaves every employment decision alone, so the same record cannot be used to
    // move someone into another department. Staged without saving — the caller owns
    // the transaction that also creates their account.
    Task StageSelfProfileAsync(
        Guid employeeId,
        SelfProfileRequest profile,
        CancellationToken cancellationToken = default);

    // Behind the row actions — Deactivate, Start Offboarding, Delete, Restore. A
    // separate call from Update so a status change is an intent, not a side effect
    // of saving a form.
    Task<EmployeeResult> ChangeStatusAsync(
        Guid id,
        EmployeeStatus status,
        CancellationToken cancellationToken = default);
}

public enum EmployeeOutcome
{
    Succeeded,
    NotFound,
    DuplicateEmail,
    DuplicateEmployeeNumber,
    UnknownDepartment,
    UnknownManager,

    // Someone cannot report to themselves, nor to one of their own reports.
    ManagerCycle,

    // A leaver cannot be quietly moved back to Active, and onboarding does not run
    // in reverse.
    InvalidStatusChange,

    // An asset in the wizard's Assets step carries a tag another asset already has.
    DuplicateAssetTag,
}

public sealed record EmployeeQuery : PagedQuery
{
    // The caller's reach, as the permission handler resolved it. A DataScope rather
    // than a breadth: "these named departments" cannot be expressed by breadth alone,
    // and until this carried the ids, a department-scoped level narrowed to nothing.
    public required DataScope Scope { get; init; }

    // The employee record of the person asking, needed for DirectReports and Self.
    public Guid? ViewerEmployeeId { get; init; }

    public string? Search { get; init; }

    public Guid? DepartmentId { get; init; }

    public EmployeeStatus? Status { get; init; }

    public EmploymentType? EmploymentType { get; init; }

    public WorkMode? WorkMode { get; init; }

    // Start date, inclusive at both ends. Either side may stand alone: "everyone who
    // joined since April" is a range with no upper bound.
    public DateOnly? StartedFrom { get; init; }

    public DateOnly? StartedTo { get; init; }
}

// The manual wizard: everything a person can say about themselves, plus the
// employment decisions only the employer makes.
public sealed record EmployeeRequest : SelfProfileRequest
{
    public required string FirstName { get; init; }

    public string? MiddleName { get; init; }

    public required string LastName { get; init; }

    public required string Email { get; init; }

    public string? EmployeeNumber { get; init; }

    public required string JobTitle { get; init; }

    public required Guid DepartmentId { get; init; }

    public required EmploymentType EmploymentType { get; init; }

    public Guid? ManagerId { get; init; }

    public DateOnly? StartDate { get; init; }

    public string? WorkLocation { get; init; }

    public WorkMode? WorkMode { get; init; }

    public string? Grade { get; init; }

    // Kit handed over at onboarding. Created in the assets module and assigned to
    // this person, in the same transaction as the employee. Admin-only: an employee
    // recording which laptop they were given is backwards.
    public IReadOnlyList<AssetRequest>? Assets { get; init; }

    public EmployeeStatus Status { get; init; } = EmployeeStatus.Pending;
}

public sealed record EmployeeResult
{
    public required EmployeeOutcome Outcome { get; init; }

    public EmployeeDto? Employee { get; init; }

    public bool Succeeded => Outcome == EmployeeOutcome.Succeeded;

    public static EmployeeResult Failed(EmployeeOutcome outcome) => new() { Outcome = outcome };

    public static EmployeeResult Ok(EmployeeDto employee) =>
        new() { Outcome = EmployeeOutcome.Succeeded, Employee = employee };
}

// What the employees table renders. Deliberately narrower than EmployeeDto so a
// list of 5,000 does not carry every personal field.
public sealed record EmployeeStatusCount
{
    public required EmployeeStatus Status { get; init; }

    public required int Count { get; init; }
}

public sealed record EmployeeStatsDto
{
    // Everyone on the books. Soft-deleted rows keep their own tab but must not
    // inflate headcount — they are a recycle bin, not people who work here.
    public required int Headcount { get; init; }

    // Every status, including the ones sitting at zero, in lifecycle order. Built
    // from the enum so a status added later cannot be forgotten here, and so the tab
    // strip does not have to know the list in advance.
    public required IReadOnlyList<EmployeeStatusCount> ByStatus { get; init; }
}

public sealed record EmployeeListItemDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Initials { get; init; }

    // Null when they have not uploaded one; the initials are the fallback.
    public Guid? AvatarFileId { get; init; }

    // A signed link minted when this record is read, not a stored URL. The bucket is
    // private, so the only URLs that work are signed ones, and those expire — one
    // saved in the database would render as a broken image a week later.
    public string? AvatarUrl { get; init; }

    public required string Email { get; init; }

    public string? Phone { get; init; }

    public string? JobTitle { get; init; }

    public Guid? DepartmentId { get; init; }

    public string? Department { get; init; }

    public EmploymentType? EmploymentType { get; init; }

    public WorkMode? WorkMode { get; init; }

    public string? WorkLocation { get; init; }

    public required EmployeeStatus Status { get; init; }

    public Guid? ManagerId { get; init; }

    public string? ManagerName { get; init; }

    public required int DirectReportCount { get; init; }

    public DateOnly? StartDate { get; init; }

    public string? EmployeeNumber { get; init; }
}

public sealed record EmployeeDto
{
    public required Guid Id { get; init; }

    public string? Title { get; init; }

    public required string FirstName { get; init; }

    public string? MiddleName { get; init; }

    public required string LastName { get; init; }

    public required string FullName { get; init; }

    public string? PreferredName { get; init; }

    public string? MaidenName { get; init; }

    public string? Initials { get; init; }

    public Guid? AvatarFileId { get; init; }

    public string? AvatarUrl { get; init; }

    public required string Email { get; init; }

    public string? Phone { get; init; }

    public DateOnly? DateOfBirth { get; init; }

    public string? Gender { get; init; }

    public string? Nationality { get; init; }

    public string? Ethnicity { get; init; }

    public string? MaritalStatus { get; init; }

    public string? Address { get; init; }

    public string? State { get; init; }

    public string? CountryOfEmployment { get; init; }

    public string? EmployeeNumber { get; init; }

    public string? JobTitle { get; init; }

    public Guid? DepartmentId { get; init; }

    public string? Department { get; init; }

    public EmploymentType? EmploymentType { get; init; }

    public Guid? ManagerId { get; init; }

    public string? ManagerName { get; init; }

    public required int DirectReportCount { get; init; }

    public required EmployeeStatus Status { get; init; }

    public DateOnly? StartDate { get; init; }

    public string? WorkLocation { get; init; }

    public WorkMode? WorkMode { get; init; }

    public string? Grade { get; init; }

    public string? EmergencyContactName { get; init; }

    public string? EmergencyContactRelationship { get; init; }

    public string? EmergencyContactPhone { get; init; }

    public string? EmergencyContactEmail { get; init; }

    // Null when nothing has been recorded. Medical is deliberately absent — it is
    // read through its own endpoint, behind its own permission.
    public BankDetailsDto? BankDetails { get; init; }

    public IdentityDocumentsDto? IdentityDocuments { get; init; }

    public required OnboardingMethod OnboardingMethod { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
