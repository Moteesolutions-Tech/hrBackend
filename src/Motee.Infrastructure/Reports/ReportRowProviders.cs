using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Motee.Application.Assets;
using Motee.Application.Attendance;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Leave;
using Motee.Application.Learning;
using Motee.Application.Performance;
using Motee.Application.Reports;
using Motee.Domain.Attendance;
using Motee.Domain.Learning;
using Motee.Domain.Performance;
using Motee.Domain.Assets;
using Motee.Domain.Authorization;
using Motee.Domain.Contracts;
using Motee.Domain.Employees;
using Motee.Domain.Leave;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Reports;


internal sealed class ReportRowProviders(
    MoteeDbContext dbContext,
    IEmployeeService employees,
    ILeaveRequestService leave,
    IAssetService assets,
    IAttendanceService attendance,
    IPerformanceReviewService reviews,
    ILearningService learning)
{

    public async Task<PagedResult<IReadOnlyDictionary<string, string?>>> RowsAsync(
        ReportDataset dataset,
        DataScope scope,
        Guid? viewerEmployeeId,
        IReadOnlyDictionary<string, string?>? filters,
        PagedQuery page,
        DateOnly asOf,
        CancellationToken cancellationToken) =>
        dataset.Key switch
        {
            "employees" => await EmployeesAsync(
                dataset, scope, viewerEmployeeId, filters, page, asOf, cancellationToken),
            "leave" => await LeaveAsync(
                dataset, scope, viewerEmployeeId, filters, page, cancellationToken),
            "attendance" => await AttendanceAsync(
                dataset, scope, viewerEmployeeId, filters, page, cancellationToken),
            "performance" => await PerformanceAsync(
                dataset, scope, viewerEmployeeId, filters, page, cancellationToken),
            "learning" => await LearningAsync(
                dataset, scope, viewerEmployeeId, filters, page, cancellationToken),
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
        DateOnly asOf,
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
                Search = Filter(filters, "fullName") ?? Filter(filters, "jobTitle"),
                DepartmentId = departmentId,
                Status = status,
                EmploymentType = employmentType,
            },
            cancellationToken);

        // Read for the narrowed set only, after the scope has done its work, so a
        // team-scoped manager's report does not pull the company's pay and dates of
        // birth out of the database on the way to being thrown away.
        EmployeeExtras extras = await ExtrasAsync(
            [.. rows.Select(employee => employee.Id)], cancellationToken);

        return Page(
            rows.Select(employee => Row(dataset, key => key switch
            {
                "employeeNumber" => employee.EmployeeNumber,
                "fullName" => employee.Name,
                "email" => employee.Email,
                "phone" => employee.Phone,
                "jobTitle" => employee.JobTitle,
                "department" => employee.Department,
                "employmentType" => employee.EmploymentType?.ToString(),
                "grade" => extras.Grade.GetValueOrDefault(employee.Id),
                "gender" => extras.Gender.GetValueOrDefault(employee.Id),
                "workMode" => employee.WorkMode?.ToString(),
                "workLocation" => employee.WorkLocation,
                "region" => Region(employee, extras),
                "manager" => employee.ManagerName,
                "directReports" => Count(employee.DirectReportCount),
                "age" => AgeOn(extras.DateOfBirth.GetValueOrDefault(employee.Id), asOf),
                "tenureYears" => TenureOn(employee.StartDate, asOf),
                "startDate" => Day(employee.StartDate),
                "status" => employee.Status.ToString(),

                // Null, not zero, when there is no contract on file: an employee whose
                // pay is not recorded has not been recorded as earning nothing.
                "salary" => extras.Salary.TryGetValue(employee.Id, out decimal pay)
                    ? Amount(pay)
                    : null,

                _ => null,
            })),
            page,

            // Applied here because the service has no work-mode filter. Safe at this
            // point: the scope and the other filters have already narrowed the set.
            workMode is null
                ? null
                : employee => employee["workMode"] == workMode.Value.ToString());
    }

    // The fields the roster report needs that the employee list DTO does not carry.
    //
    // Deliberately not added to EmployeeListItemDto, which every employee screen reads:
    // gender, date of birth and pay do not belong in a general list payload, and putting
    // them there would widen who sees them to everybody holding View on employees. Here
    // they are read once, for one report, behind that report's own gates.
    private sealed record EmployeeExtras
    {
        public required IReadOnlyDictionary<Guid, string?> Gender { get; init; }

        public required IReadOnlyDictionary<Guid, string?> Grade { get; init; }

        public required IReadOnlyDictionary<Guid, DateOnly?> DateOfBirth { get; init; }

        public required IReadOnlyDictionary<Guid, Guid?> BranchId { get; init; }

        public required IReadOnlyDictionary<Guid, string?> BranchRegion { get; init; }

        public required IReadOnlyDictionary<Guid, decimal> Salary { get; init; }
    }

    private async Task<EmployeeExtras> ExtrasAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new EmployeeExtras
            {
                Gender = new Dictionary<Guid, string?>(),
                Grade = new Dictionary<Guid, string?>(),
                DateOfBirth = new Dictionary<Guid, DateOnly?>(),
                BranchId = new Dictionary<Guid, Guid?>(),
                BranchRegion = new Dictionary<Guid, string?>(),
                Salary = new Dictionary<Guid, decimal>(),
            };
        }

        List<EmployeeFacts> facts = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => ids.Contains(employee.Id))
            .Select(employee => new EmployeeFacts
            {
                Id = employee.Id,
                Gender = employee.Gender,
                Grade = employee.Grade,
                DateOfBirth = employee.DateOfBirth,
                BranchId = employee.BranchId,
            })
            .ToListAsync(cancellationToken);

        // Region prefers the branch's own region and falls back to its city, matching
        // the chain the reports screen uses.
        Dictionary<Guid, string?> regions = await dbContext.Branches
            .AsNoTracking()
            .ToDictionaryAsync(
                branch => branch.Id,
                branch => branch.Region ?? branch.City,
                cancellationToken);

        // The active contract, latest first where somebody has more than one — a
        // renewal signed before the old one ends is the normal case, and the new terms
        // are the ones that answer "what do they earn".
        List<ContractPay> pay = await dbContext.Contracts
            .AsNoTracking()
            .Where(contract =>
                ids.Contains(contract.EmployeeId)
                && contract.Status == ContractStatus.Active
                && !contract.IsArchived
                && contract.Salary != null)
            .OrderByDescending(contract => contract.StartDate)
            .Select(contract => new ContractPay
            {
                EmployeeId = contract.EmployeeId,
                Salary = contract.Salary!.Value,
            })
            .ToListAsync(cancellationToken);

        return new EmployeeExtras
        {
            Gender = facts.ToDictionary(fact => fact.Id, fact => fact.Gender),
            Grade = facts.ToDictionary(fact => fact.Id, fact => fact.Grade),
            DateOfBirth = facts.ToDictionary(fact => fact.Id, fact => fact.DateOfBirth),
            BranchId = facts.ToDictionary(fact => fact.Id, fact => fact.BranchId),
            BranchRegion = regions,

            // First wins, and the list is already ordered newest first.
            Salary = pay
                .GroupBy(row => row.EmployeeId)
                .ToDictionary(group => group.Key, group => group.First().Salary),
        };
    }

    private sealed record EmployeeFacts
    {
        public required Guid Id { get; init; }

        public string? Gender { get; init; }

        public string? Grade { get; init; }

        public DateOnly? DateOfBirth { get; init; }

        public Guid? BranchId { get; init; }
    }

    private sealed record ContractPay
    {
        public required Guid EmployeeId { get; init; }

        public required decimal Salary { get; init; }
    }

    // Branch region, then branch city, then the employee's own free-text work location.
    // "Unspecified" rather than blank at the end, because a region chart with an unnamed
    // slice is worse than one with an honest bucket.
    private static string Region(EmployeeListItemDto employee, EmployeeExtras extras)
    {
        string? region = employee.BranchId is Guid branchId
            ? extras.BranchRegion.GetValueOrDefault(branchId)
            : null;

        return Trimmed(region) ?? Trimmed(employee.WorkLocation) ?? "Unspecified";
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Calendar age, not a division by an average year: somebody born on 29 February is
    // the age their birthday says, and a report that disagrees with a passport is the
    // kind of detail that gets noticed.
    //
    // Empty rather than 0 when there is no date of birth. A chart bucketing a missing
    // age as a newborn is worse than one leaving them out.
    private static string AgeOn(DateOnly? dateOfBirth, DateOnly asOf)
    {
        if (dateOfBirth is not DateOnly born || born > asOf)
        {
            return string.Empty;
        }

        int age = asOf.Year - born.Year;

        if (born.AddYears(age) > asOf)
        {
            age--;
        }

        return Count(age);
    }

    // Tenure genuinely is a duration rather than a count of birthdays, so the average
    // year is the right divisor. One decimal place, as the screen shows it.
    private static string TenureOn(DateOnly? startDate, DateOnly asOf)
    {
        if (startDate is not DateOnly started || started > asOf)
        {
            return string.Empty;
        }

        double years = asOf.DayNumber - started.DayNumber;

        return Math.Round(years / 365.25, 1).ToString("0.#", CultureInfo.InvariantCulture);
    }

    // ── leave ───────────────────────────────────────────────────────────────

    private async Task<PagedResult<IReadOnlyDictionary<string, string?>>> LeaveAsync(
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

        Guid? leaveTypeId = await LeaveTypeIdAsync(
            Filter(filters, "type"), cancellationToken);

        if (Filter(filters, "type") is not null && leaveTypeId is null)
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
                Search = Filter(filters, "employee"),
                Page = page.Page,
                PageSize = page.PageSize,
            },

            // The report's own scope, which this dataset was not passing before: the
            // leave register took no viewer, so a department-scoped HR user running the
            // Leave report got the whole company's absence and reasons with it.
            Viewer.Of(scope, viewerEmployeeId),
            cancellationToken);

        // Already paged by the service, so the page is carried through rather than
        // recomputed — re-paging a page would silently drop rows.
        return new PagedResult<IReadOnlyDictionary<string, string?>>
        {
            Items =
            [
                .. rows.Items.Select(request => Row(dataset, key => key switch
                {
                    "employee" => request.EmployeeName,
                    "department" => request.DepartmentName,
                    "type" => request.LeaveTypeName,
                    "startDate" => Day(request.StartDate),
                    "endDate" => Day(request.EndDate),
                    "days" => Amount(request.TotalDays),
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

    // ── attendance ──────────────────────────────────────────────────────────

    // One day at a time, which is what the attendance register is: AttendanceQuery
    // takes a single date and the service defaults it to the tenant's today. The date
    // column is filterable, so a report can ask for another day, but a range would need
    // From/To on the module's own query rather than a second reader here — the point of
    // reading through the module is that the report and the screen cannot disagree.
    private async Task<PagedResult<IReadOnlyDictionary<string, string?>>> AttendanceAsync(
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

        if (!TryParse<AttendanceStatus>(
                Filter(filters, "status"), out AttendanceStatus? status))
        {
            return Empty(page);
        }

        DateOnly? date = Date(Filter(filters, "date"));

        if (Filter(filters, "date") is not null && date is null)
        {
            return Empty(page);
        }

        PagedResult<AttendanceDayDto> rows = await attendance.ListAsync(
            new AttendanceQuery
            {
                DepartmentId = departmentId,
                Status = status,
                Date = date,
                Page = page.Page,
                PageSize = page.PageSize,
            },
            Viewer.Of(scope, viewerEmployeeId),
            cancellationToken);

        return new PagedResult<IReadOnlyDictionary<string, string?>>
        {
            Items =
            [
                .. rows.Items.Select(day => Row(dataset, key => key switch
                {
                    "date" => Day(day.Date),
                    "employee" => day.EmployeeName,
                    "department" => day.DepartmentName,
                    "status" => day.Status.ToString(),

                    // The clock times as the tenant would read them, not as instants:
                    // a column headed "Clock In" wants 08:57, and the offset is noise
                    // in a spreadsheet.
                    "clockIn" => Clock(day.ClockInAt),
                    "clockOut" => Clock(day.ClockOutAt),

                    // Worked rather than scheduled: the register's question is how long
                    // people were actually here.
                    "hours" => Amount(day.WorkedHours),

                    _ => null,
                })),
            ],
            Page = rows.Page,
            PageSize = rows.PageSize,
            TotalItems = rows.TotalItems,
        };
    }

    // ── performance ─────────────────────────────────────────────────────────

    private async Task<PagedResult<IReadOnlyDictionary<string, string?>>> PerformanceAsync(
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

        if (!TryParse<ReviewStatus>(Filter(filters, "status"), out ReviewStatus? status))
        {
            return Empty(page);
        }

        PagedResult<PerformanceReviewDto> rows = await reviews.ListAsync(
            new ReviewQuery
            {
                DepartmentId = departmentId,
                Status = status,
                Page = page.Page,
                PageSize = page.PageSize,
            },
            Viewer.Of(scope, viewerEmployeeId),
            cancellationToken);

        return new PagedResult<IReadOnlyDictionary<string, string?>>
        {
            Items =
            [
                .. rows.Items.Select(review => Row(dataset, key => key switch
                {
                    "employee" => review.EmployeeName,
                    "department" => review.DepartmentName,

                    // The cycle this review belongs to, as the screen labels it.
                    "cycle" => review.Period,

                    // Null, not zero, for a rating nobody has given: an unrated review
                    // averaged as zero would drag a department's score down for being
                    // incomplete rather than poor.
                    "self" => Count(review.SelfRating),
                    "manager" => Count(review.ManagerRating),
                    "calibrated" => Count(review.CalibratedRating),

                    "status" => review.Status.ToString(),
                    "completedAt" => Day(review.CompletedAt),
                    _ => null,
                })),
            ],
            Page = rows.Page,
            PageSize = rows.PageSize,
            TotalItems = rows.TotalItems,
        };
    }

    // ── learning ────────────────────────────────────────────────────────────

    private async Task<PagedResult<IReadOnlyDictionary<string, string?>>> LearningAsync(
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

        if (!TryParse<EnrollmentStatus>(
                Filter(filters, "status"), out EnrollmentStatus? status))
        {
            return Empty(page);
        }

        PagedResult<EnrollmentDto> rows = await learning.EnrollmentsAsync(
            new EnrollmentQuery
            {
                DepartmentId = departmentId,
                Status = status,

                // Employee and course share the service's one search box.
                Search = Filter(filters, "employee") ?? Filter(filters, "course"),
                Page = page.Page,
                PageSize = page.PageSize,
            },
            Viewer.Of(scope, viewerEmployeeId),
            cancellationToken);

        // The course's category, which the enrolment does not carry. Read for the page's
        // courses only, so a completion-by-category chart needs no second call from the
        // client.
        List<Guid> courses = [.. rows.Items.Select(row => row.CourseId).Distinct()];

        Dictionary<Guid, string> categories = courses.Count == 0
            ? []
            : await dbContext.Courses
                .AsNoTracking()
                .Where(course => courses.Contains(course.Id))
                .ToDictionaryAsync(
                    course => course.Id,
                    course => course.Category.ToString(),
                    cancellationToken);

        return new PagedResult<IReadOnlyDictionary<string, string?>>
        {
            Items =
            [
                .. rows.Items.Select(enrolment => Row(dataset, key => key switch
                {
                    "employee" => enrolment.EmployeeName,
                    "course" => enrolment.CourseTitle,
                    "category" => categories.GetValueOrDefault(enrolment.CourseId),
                    "status" => enrolment.Status.ToString(),
                    "progress" => Count(enrolment.Progress),
                    "enrolledAt" => Day(enrolment.EnrolledOn),
                    "mandatory" => enrolment.IsMandatory ? "true" : "false",
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

        if (!TryParse<AssetCondition>(
                Filter(filters, "condition"), out AssetCondition? condition))
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
                Condition = condition,

                // Tag and holder share the service's search box.
                Search = Filter(filters, "assetTag") ?? Filter(filters, "assignedTo"),
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
                    "assetTag" => asset.Tag,
                    "name" => asset.Name,
                    "category" => asset.Category,
                    "serialNumber" => asset.SerialNumber,
                    "condition" => asset.Condition?.ToString(),
                    "assignedTo" => asset.AssignedToName,
                    "assignedDate" => Day(asset.AssignedDate),
                    "status" => asset.Status.ToString(),

                    // Null, not zero, when nobody priced it: an unpriced asset has not
                    // been recorded as worthless, and a total that counts it as zero
                    // understates the replacement cost.
                    "value" => asset.Value is decimal worth ? Amount(worth) : null,

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

    // An instant read as a day. A Date column carries yyyy-MM-dd whatever the source
    // type, so a grid and a CSV do not have to tell the two apart.
    private static string Day(DateTimeOffset? value) =>
        value is DateTimeOffset at
            ? DateOnly.FromDateTime(at.UtcDateTime).ToString(
                "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;

    // The wall-clock time, not the instant: a column headed "Clock In" wants 08:57, and
    // the offset is noise in a spreadsheet.
    private static string Clock(DateTimeOffset? value) =>
        value?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    // Empty, not "0", for a figure nobody has given. A rating averaged as zero would
    // drag a department's score down for being incomplete rather than poor.
    private static string Count(int? value) =>
        value is int number ? Count(number) : string.Empty;

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
