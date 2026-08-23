using Motee.Domain.Common;
using Motee.Domain.Organisation;

namespace Motee.Domain.Employees;

public class Employee : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The tenant's own identifier for this person, unique within the tenant. Distinct
    // from Id, which is ours.
    public string? EmployeeNumber { get; set; }

    public required string FirstName { get; set; }

    public string? MiddleName { get; set; }

    public required string LastName { get; set; }

    public string? Title { get; set; }

    // What they go by, when it differs from their legal first name.
    public string? PreferredName { get; set; }

    public string? MaidenName { get; set; }

    // Stored rather than derived. Three names give "JMD", and someone who signs
    // "A.O." is not going to accept what a substring would produce.
    public string? Initials { get; set; }

    // The stored file, not a URL. A URL would be stale the moment storage moves, and
    // a signed one expires — so the link is minted when the record is read.
    public Guid? AvatarFileId { get; set; }

    public required string Email { get; set; }

    public string? Phone { get; set; }

    public string? JobTitle { get; set; }

    public Guid? DepartmentId { get; set; }

    // A fixed taxonomy rather than a reference — see EmploymentType. Null until the
    // Employment step is filled in; the create endpoint requires it.
    public EmploymentType? EmploymentType { get; set; }

    // Self-referencing: this is the org hierarchy that PermissionScope.Team narrows
    // against, and that approval routing walks to resolve LINE_MANAGER.
    public Guid? ManagerId { get; set; }

    public EmployeeStatus Status { get; set; } = EmployeeStatus.Pending;

    public DateOnly? StartDate { get; set; }

    public DateOnly? DateOfLeaving { get; set; }

    public DateOnly? DateOfBirth { get; set; }

    public string? Gender { get; set; }

    public string? Nationality { get; set; }

    public string? MaritalStatus { get; set; }

    public string? Ethnicity { get; set; }

    public string? Address { get; set; }

    public string? State { get; set; }

    // Free text like Nationality — people write "Nigeria", "NG" or "Republic of
    // Ireland", and none of it drives behaviour. The tenant's CountryCode is the one
    // that governs jurisdiction.
    public string? CountryOfEmployment { get; set; }

    public string? WorkLocation { get; set; }

    public WorkMode? WorkMode { get; set; }

    public string? Grade { get; set; }

    // Exactly one per employee and always read with them, so columns rather than a
    // table. The form requires all three.
    public string? EmergencyContactName { get; set; }

    public string? EmergencyContactRelationship { get; set; }

    public string? EmergencyContactPhone { get; set; }

    public string? EmergencyContactEmail { get; set; }

    public OnboardingMethod OnboardingMethod { get; set; } = OnboardingMethod.Manual;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string FullName => string.IsNullOrWhiteSpace(MiddleName)
        ? $"{FirstName} {LastName}"
        : $"{FirstName} {MiddleName} {LastName}";
}
