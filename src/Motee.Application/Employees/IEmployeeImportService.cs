using Motee.Domain.Employees;
using Motee.Domain.Organisation;

namespace Motee.Application.Employees;

public interface IEmployeeImportService
{
    Task<EmployeeImportResult> ImportAsync(
        IReadOnlyList<EmployeeImportRow> rows,
        bool sendInvitations = true,
        CancellationToken cancellationToken = default);
}

public sealed record EmployeeImportRequest
{
    public required IReadOnlyList<EmployeeImportRow> Rows { get; init; }

    // Everyone imported is emailed a link to set a password, so a file of 200 people
    // is not followed by 200 clicks. Set false when migrating historical records that
    // nobody should be emailed about yet.
    public bool SendInvitations { get; init; } = true;
}

// A row as it appears in the spreadsheet: flat, and naming departments and managers
// the way a person would. Resolving those to ids is the server's job — the frontend
// would otherwise have to fetch every department and every employee, match per row,
// and invent its own error reporting for what the import already reports by line.
public sealed record EmployeeImportRow
{
    public string? EmployeeNumber { get; init; }

    public required string FirstName { get; init; }

    public string? MiddleName { get; init; }

    public required string LastName { get; init; }

    public required string Email { get; init; }

    // Optional here, unlike the manual form. Historical staff being migrated in often
    // have no phone number on file, and refusing the row over it helps nobody.
    public string? Phone { get; init; }

    public required string JobTitle { get; init; }

    // By name, as typed. Case and surrounding spaces are ignored.
    public required string Department { get; init; }

    // Email or full name. Email is unambiguous; a name is matched against existing
    // staff and anyone imported earlier in the same file.
    public string? Manager { get; init; }

    public EmploymentType? EmploymentType { get; init; }

    public DateOnly? StartDate { get; init; }

    // Importing existing staff means recording people who are already Active, so that
    // is the default rather than Pending.
    public EmployeeStatus? Status { get; init; }

    public string? WorkLocation { get; init; }

    public WorkMode? WorkMode { get; init; }

    public string? Grade { get; init; }

    // One piece of kit per row, matching the onboarding template. Anything more goes
    // through the assets module.
    public string? AssetTag { get; init; }

    public string? AssetName { get; init; }

    public string? AssetCategory { get; init; }

    public string? AssetSerialNumber { get; init; }

    public DateOnly? AssetAssignedDate { get; init; }
}

public sealed record EmployeeImportResult
{
    public required int Imported { get; init; }

    public required int Failed { get; init; }

    // How many were emailed a link to set a password. Lower than Imported when
    // someone already had an account, or was imported as a leaver.
    public required int Invited { get; init; }

    // Only the rows that did not import. A clean upload returns an empty list rather
    // than 200 successes nobody reads.
    public required IReadOnlyList<EmployeeImportError> Errors { get; init; }
}

public sealed record EmployeeImportError
{
    // 1-based position in the uploaded file, so it points at a line the user can see.
    public required int Row { get; init; }

    public string? Email { get; init; }

    public required string Message { get; init; }
}
