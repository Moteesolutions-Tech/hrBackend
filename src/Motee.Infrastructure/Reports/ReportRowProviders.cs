using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Motee.Application.Assets;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Leave;
using Motee.Application.Reports;
using Motee.Domain.Assets;
using Motee.Domain.Authorization;
using Motee.Domain.Employees;
using Motee.Domain.Leave;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Reports;

// Where a report's rows come from.
//
// Each dataset reads its own module's list service rather than a fresh query, so a
// report and the screen it reports on cannot disagree — the same reason
// /dashboard/mine delegates for its clock. That also means every narrowing those
// services already do, including the caller's data scope, applies to the report for
// free.
//
// The work here is the translation. A report filter arrives as a column key and a
// string — "department" = "Engineering" — while each module's query is typed, wanting
// a Guid for the department and an enum for the status. That mapping is
// provider-specific and belongs beside the provider, not in the Application catalogue,
// which only declares what exists.
//
// A value that does not translate is not silently dropped: the provider returns no rows
// rather than every row. Asking for status "Purple" and getting the whole table back
// would be the worst of the available answers.
internal sealed class ReportRowProviders(
    MoteeDbContext dbContext,
    IEmployeeService employees,
    ILeaveRequestService leave,
    IAssetService assets)
{
    public async Task<PagedResult<IReadOnlyDictionary<string, string?>>> RowsAsync(
        ReportDataset dataset,
        DataScope scope,
        Guid? viewerEmployeeId,
        IReadOnlyDictionary<string, string?>? filters,
        PagedQuery page,
        CancellationToken cancellationToken) =>
        dataset.Key switch
        {
            "employees" => await EmployeesAsync(
                dataset, scope, viewerEmployeeId, filters, page, cancellationToken),
            "leave" => await LeaveAsync(dataset, filters, page, cancellationToken),
            "assets" => await AssetsAsync(
                dataset, scope, viewerEmployeeId, filters, page, cancellationToken),

            // Unreachable through the service, which checks the catalogue first. Here
            // so that adding a dataset to the catalogue without a provider fails loudly
            // in a test rather than quietly returning an empty report.
            _ => throw new InvalidOperationException(
                $"No row provider for report dataset '{dataset.Key}'."),
        };

    // ── employees ───────────────────────────────────────────────────────────

    private async Task<PagedResult<IReadOnlyDictionary<string, string?>>> EmployeesAsync(
        ReportDataset dataset,
        DataScope scope,
        Guid? viewerEmployeeId,
        IReadOnlyDictionary<string, string?>? filters,
        PagedQuery page,
        CancellationToken cancellationToken)
    {
        Guid? departmentId = await DepartmentIdAsync(
            Filter(filters, "department"), cancellationToken);

        if (Filter(filters, "department") is not null && departmentId is null)
        {
            return Empty(page);
        }

        if (!TryParse<EmployeeStatus>(Filter(filters, "status"), out EmployeeStatus? status))
        {
            return Empty(page);
        }

        if (!TryParse<EmploymentType>(
                Filter(filters, "employmentType"), out EmploymentType? employmentType))
        {
            return Empty(page);
        }

        if (!TryParse<WorkMode>(Filter(filters, "workMode"), out WorkMode? workMode))
        {
            return Empty(page);
        }

        // Unpaged from the service, then paged here. ExportAsync is the unpaged read —
        // the same one the CSV export uses — because a report's filters narrow after
        // the scope does, and paging before that would give short pages.
        IReadOnlyList<EmployeeListItemDto> rows = await employees.ExportAsync(
            new EmployeeQuery
            {
                // The caller's own scope, resolved per dataset. Not Everything: a
                // team-scoped manager's report covers their team, exactly as their
                // screen does.
                Scope = scope,
                ViewerEmployeeId = viewerEmployeeId,

                // Name and job title share the service's one search box, which matches
                // across both. Narrower than the column implies, and the alternative is
                // filtering in memory over the whole roster.
                Search = Filter(filters, "name") ?? Filter(filters, "jobTitle"),
                DepartmentId = departmentId,
                Status = status,
                EmploymentType = employmentType,
            },
            cancellationToken);

        return Page(
            rows.Select(employee => Row(dataset, key => key switch
            {
                "employeeNumber" => employee.EmployeeNumber,
                "name" => employee.Name,
                "email" => employee.Email,
                "phone" => employee.Phone,
                "jobTitle" => employee.JobTitle,
                "department" => employee.Department,
                "employmentType" => employee.EmploymentType?.ToString(),
                "workMode" => employee.WorkMode?.ToString(),
                "workLocation" => employee.WorkLocation,
                "manager" => employee.ManagerName,
                "directReports" => Count(employee.DirectReportCount),
                "startDate" => Day(employee.StartDate),
                "status" => employee.Status.ToString(),
                _ => null,
            })),
            page,

            // Applied here because the service has no work-mode filter. Safe at this
            // point: the scope and the other filters have already narrowed the set.
            workMode is null
                ? null
                : employee => employee["workMode"] == workMode.Value.ToString());
    }

    // ── leave ───────────────────────────────────────────────────────────────

    private async Task<PagedResult<IReadOnlyDictionary<string, string?>>> LeaveAsync(
        ReportDataset dataset,
        IReadOnlyDictionary<string, string?>? filters,
        PagedQuery page,
        CancellationToken cancellationToken)
    {
        Guid? departmentId = await DepartmentIdAsync(
            Filter(filters, "department"), cancellationToken);

        if (Filter(filters, "department") is not null && departmentId is null)
        {
            return Empty(page);
        }

        Guid? leaveTypeId = await LeaveTypeIdAsync(
            Filter(filters, "leaveType"), cancellationToken);

        if (Filter(filters, "leaveType") is not null && leaveTypeId is null)
        {
            return Empty(page);
        }

        if (!TryParse<LeaveRequestStatus>(
                Filter(filters, "status"), out LeaveRequestStatus? status))
        {
            return Empty(page);
        }

        DateOnly? from = Date(Filter(filters, "startDate"));

        if (Filter(filters, "startDate") is not null && from is null)
        {
            return Empty(page);
        }

        PagedResult<LeaveRequestDto> rows = await leave.ListAsync(
            new LeaveRequestQuery
            {
                DepartmentId = departmentId,
                LeaveTypeId = leaveTypeId,
                Status = status,
                From = from,
                Search = Filter(filters, "employeeName"),
                Page = page.Page,
                PageSize = page.PageSize,
            },
            cancellationToken);

        // Already paged by the service, so the page is carried through rather than
        // recomputed — re-paging a page would silently drop rows.
        return new PagedResult<IReadOnlyDictionary<string, string?>>
        {
            Items =
            [
                .. rows.Items.Select(request => Row(dataset, key => key switch
                {
                    "employeeName" => request.EmployeeName,
                    "department" => request.DepartmentName,
                    "leaveType" => request.LeaveTypeName,
                    "startDate" => Day(request.StartDate),
                    "endDate" => Day(request.EndDate),
                    "totalDays" => Amount(request.TotalDays),
                    "isHalfDay" => request.IsHalfDay ? "true" : "false",
                    "status" => request.Status.ToString(),
                    "reason" => request.Reason,
                    "relief" => request.ReliefEmployeeName,
                    _ => null,
                })),
            ],
            Page = rows.Page,
            PageSize = rows.PageSize,
            TotalItems = rows.TotalItems,
        };
    }

    // ── assets ──────────────────────────────────────────────────────────────

    private async Task<PagedResult<IReadOnlyDictionary<string, string?>>> AssetsAsync(
        ReportDataset dataset,
        DataScope scope,
        Guid? viewerEmployeeId,
        IReadOnlyDictionary<string, string?>? filters,
        PagedQuery page,
        CancellationToken cancellationToken)
    {
        if (!TryParse<AssetStatus>(Filter(filters, "status"), out AssetStatus? status))
        {
            return Empty(page);
        }

        PagedResult<AssetDto> rows = await assets.ListAsync(
            new AssetQuery
            {
                Scope = scope,
                ViewerEmployeeId = viewerEmployeeId,
                Category = Filter(filters, "category"),
                Status = status,

                // Tag and holder share the service's search box.
                Search = Filter(filters, "tag") ?? Filter(filters, "assignedTo"),
                Page = page.Page,
                PageSize = page.PageSize,
            },
            cancellationToken);

        return new PagedResult<IReadOnlyDictionary<string, string?>>
        {
            Items =
            [
                .. rows.Items.Select(asset => Row(dataset, key => key switch
                {
                    "tag" => asset.Tag,
                    "name" => asset.Name,
                    "category" => asset.Category,
                    "serialNumber" => asset.SerialNumber,
                    "assignedTo" => asset.AssignedToName,
                    "assignedDate" => Day(asset.AssignedDate),
                    "status" => asset.Status.ToString(),
                    _ => null,
                })),
            ],
            Page = rows.Page,
            PageSize = rows.PageSize,
            TotalItems = rows.TotalItems,
        };
    }

    // ── shared ──────────────────────────────────────────────────────────────

    // Only the columns asked for, in the order asked. Building every column and letting
    // the client pick would put a phone number in a payload somebody deliberately left
    // out of their report.
    private static IReadOnlyDictionary<string, string?> Row(
        ReportDataset dataset,
        Func<string, string?> read)
    {
        Dictionary<string, string?> row = new(StringComparer.Ordinal);

        foreach (ReportColumn column in dataset.Columns)
        {
            row[column.Key] = read(column.Key);
        }

        return row;
    }

    private static string? Filter(IReadOnlyDictionary<string, string?>? filters, string key)
    {
        if (filters is null)
        {
            return null;
        }

        string? value = filters.FirstOrDefault(pair =>
            string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    // False when a value was sent and does not parse, which the caller turns into no
    // rows. Asking for status "Purple" and getting the whole table back would be the
    // worst of the available answers.
    private static bool TryParse<T>(string? value, out T? parsed) where T : struct, Enum
    {
        parsed = null;

        if (value is null)
        {
            return true;
        }

        if (!Enum.TryParse(value, ignoreCase: true, out T result))
        {
            return false;
        }

        parsed = result;

        return true;
    }

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateOnly parsed)
            ? parsed
            : null;

    // Resolved by name, because that is what a report filter carries and what somebody
    // typed. Exact and case-insensitive: a contains would make "Engineering" ambiguous
    // in a company with "Engineering" and "Engineering Support".
    private async Task<Guid?> DepartmentIdAsync(string? name, CancellationToken cancellationToken)
    {
        if (name is null)
        {
            return null;
        }

        // ILike with no wildcards: a case-insensitive exact match, which is what
        // Postgres gives and what the rest of this codebase uses for the same job. The
        // escape stops a name containing % or _ from becoming a pattern.
        List<Guid> matches = await dbContext.Departments
            .AsNoTracking()
            .Where(department => EF.Functions.ILike(department.Name, Escaped(name), "\\"))
            .Select(department => department.Id)
            .ToListAsync(cancellationToken);

        // Exactly one, or nothing. Two departments matching one name is a company where
        // the filter is ambiguous, and picking the first would answer a question nobody
        // asked.
        return matches.Count == 1 ? matches[0] : null;
    }

    private async Task<Guid?> LeaveTypeIdAsync(string? name, CancellationToken cancellationToken)
    {
        if (name is null)
        {
            return null;
        }

        List<Guid> matches = await dbContext.LeaveTypes
            .AsNoTracking()
            .Where(type => EF.Functions.ILike(type.Name, Escaped(name), "\\"))
            .Select(type => type.Id)
            .ToListAsync(cancellationToken);

        return matches.Count == 1 ? matches[0] : null;
    }

    // A literal, not a pattern. Without this a department called "R_D" would match
    // "RAD" and "R&D" alike, because _ is a single-character wildcard.
    private static string Escaped(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string Day(DateOnly? value) =>
        value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Amount(decimal value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private static PagedResult<IReadOnlyDictionary<string, string?>> Empty(PagedQuery page) =>
        PagedResult<IReadOnlyDictionary<string, string?>>.Empty(page.Page, page.PageSize);

    // Pages an unpaged set, optionally narrowing first.
    private static PagedResult<IReadOnlyDictionary<string, string?>> Page(
        IEnumerable<IReadOnlyDictionary<string, string?>> rows,
        PagedQuery page,
        Func<IReadOnlyDictionary<string, string?>, bool>? narrow)
    {
        List<IReadOnlyDictionary<string, string?>> all =
            [.. narrow is null ? rows : rows.Where(narrow)];

        return new PagedResult<IReadOnlyDictionary<string, string?>>
        {
            Items = [.. all.Skip(page.Skip).Take(page.PageSize)],
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = all.Count,
        };
    }
}
