using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Motee.Application.Assets;
using Motee.Application.Employees;
using Motee.Domain.Employees;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Employees;

internal sealed class EmployeeImportService(
    MoteeDbContext dbContext,
    IEmployeeService employees,
    IEmployeeInvitationService invitations,
    IValidator<EmployeeImportRow> validator) : IEmployeeImportService
{
    public async Task<EmployeeImportResult> ImportAsync(
        IReadOnlyList<EmployeeImportRow> rows,
        bool sendInvitations = true,
        CancellationToken cancellationToken = default)
    {
        List<EmployeeImportError> errors = [];
        int imported = 0;
        int invited = 0;

        // Departments are read once rather than per row: a file of 200 people rarely
        // spans more than a handful, and 200 lookups for the same five names is waste.
        Dictionary<string, Guid> departments = await DepartmentsByNameAsync(cancellationToken);

        // Row by row rather than one transaction. A file of 200 with three typos is
        // the normal case, and rejecting all of it means fixing one cell and
        // re-uploading everything.
        for (int index = 0; index < rows.Count; index++)
        {
            EmployeeImportRow row = rows[index];
            int rowNumber = index + 1;

            ValidationResult validation = await validator.ValidateAsync(row, cancellationToken);

            if (!validation.IsValid)
            {
                errors.Add(Error(rowNumber, row,
                    string.Join(" ", validation.Errors.Select(error => error.ErrorMessage))));

                continue;
            }

            if (!departments.TryGetValue(Key(row.Department), out Guid departmentId))
            {
                errors.Add(Error(rowNumber, row,
                    $"No department named \"{row.Department.Trim()}\". "
                    + "Create it before importing, or correct the spelling."));

                continue;
            }

            // Resolved per row, not up front: a manager may be someone imported a few
            // lines earlier in this same file.
            (Guid? managerId, string? managerProblem) =
                await ResolveManagerAsync(row.Manager, cancellationToken);

            if (managerProblem is not null)
            {
                errors.Add(Error(rowNumber, row, managerProblem));
                continue;
            }

            EmployeeResult result = await employees.CreateAsync(
                ToRequest(row, departmentId, managerId), OnboardingMethod.Bulk, cancellationToken);

            if (result.Succeeded)
            {
                imported++;

                // A password link, not an onboarding one — the record is complete, so
                // there is nothing for them to fill in and nothing to show them. A
                // send that does not happen is not a failed import: the person is on
                // the books, and HR can send it again from the row actions.
                if (sendInvitations
                    && (await invitations.IssueAsync(result.Employee!.Id, cancellationToken))
                        .Succeeded)
                {
                    invited++;
                }

                continue;
            }

            errors.Add(Error(rowNumber, row, MessageFor(result.Outcome)));
        }

        return new EmployeeImportResult
        {
            Imported = imported,
            Failed = errors.Count,
            Invited = invited,
            Errors = errors,
        };
    }

    private async Task<Dictionary<string, Guid>> DepartmentsByNameAsync(
        CancellationToken cancellationToken)
    {
        List<(string Name, Guid Id)> all = await dbContext.Departments
            .AsNoTracking()
            .Select(department => new ValueTuple<string, Guid>(department.Name, department.Id))
            .ToListAsync(cancellationToken);

        Dictionary<string, Guid> byName = [];

        foreach ((string name, Guid id) in all)
        {
            // Two departments cannot share a name within a tenant, so the first wins
            // and there is nothing to disambiguate.
            byName.TryAdd(Key(name), id);
        }

        return byName;
    }

    // Email is unambiguous. A name is not — two people called Chidi Okonkwo is a
    // normal thing in a company of any size, and silently picking one of them would
    // put someone under the wrong manager.
    private async Task<(Guid? ManagerId, string? Problem)> ResolveManagerAsync(
        string? manager,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manager))
        {
            return (null, null);
        }

        string wanted = manager.Trim();

        if (wanted.Contains('@', StringComparison.Ordinal))
        {
            Guid found = await dbContext.Employees
                .Where(employee => employee.Email.ToUpper() == wanted.ToUpper(null))
                .Select(employee => employee.Id)
                .FirstOrDefaultAsync(cancellationToken);

            return found == Guid.Empty
                ? (null, $"No employee with the email \"{wanted}\" to report to.")
                : (found, null);
        }

        List<Guid> matches = await dbContext.Employees
            .Where(employee =>
                (employee.FirstName + " " + employee.LastName).ToUpper() == wanted.ToUpper(null))
            .Select(employee => employee.Id)
            .Take(2)
            .ToListAsync(cancellationToken);

        return matches.Count switch
        {
            0 => (null, $"No employee named \"{wanted}\" to report to. "
                + "Managers must appear above their reports in the file."),

            1 => (matches[0], null),

            _ => (null, $"More than one employee is named \"{wanted}\". "
                + "Use their email address instead."),
        };
    }

    private static EmployeeRequest ToRequest(
        EmployeeImportRow row,
        Guid departmentId,
        Guid? managerId) => new()
    {
        EmployeeNumber = row.EmployeeNumber,
        FirstName = row.FirstName,
        MiddleName = row.MiddleName,
        LastName = row.LastName,
        Email = row.Email,
        Phone = row.Phone ?? string.Empty,
        JobTitle = row.JobTitle,
        DepartmentId = departmentId,
        ManagerId = managerId,
        EmploymentType = row.EmploymentType!.Value,
        StartDate = row.StartDate,
        WorkLocation = row.WorkLocation,
        WorkMode = row.WorkMode,
        Grade = row.Grade,

        // An import is a record of people who already work here, so they are Active
        // unless the file says otherwise. Pending would put the whole company into an
        // onboarding pipeline they finished years ago.
        Status = row.Status ?? EmployeeStatus.Active,
        Assets = AssetsFor(row),
    };

    private static IReadOnlyList<AssetRequest>? AssetsFor(EmployeeImportRow row) =>
        string.IsNullOrWhiteSpace(row.AssetTag)
            ? null
            : [
                new AssetRequest
                {
                    Tag = row.AssetTag,
                    Name = row.AssetName!,
                    Category = row.AssetCategory,
                    SerialNumber = row.AssetSerialNumber,
                    AssignedDate = row.AssetAssignedDate ?? row.StartDate,
                },
            ];

    private static EmployeeImportError Error(int row, EmployeeImportRow source, string message) =>
        new() { Row = row, Email = source.Email, Message = message };

    private static string Key(string value) => value.Trim().ToUpperInvariant();

    private static string MessageFor(EmployeeOutcome outcome) => outcome switch
    {
        EmployeeOutcome.DuplicateEmail => "An employee with that email already exists.",
        EmployeeOutcome.DuplicateEmployeeNumber => "That employee ID is already in use.",
        EmployeeOutcome.UnknownDepartment => "That department does not exist.",
        EmployeeOutcome.UnknownManager => "That manager does not exist.",
        EmployeeOutcome.ManagerCycle => "That manager would create a reporting loop.",
        EmployeeOutcome.DuplicateAssetTag => "That asset tag is already in use.",
        _ => "Could not import this row.",
    };
}
