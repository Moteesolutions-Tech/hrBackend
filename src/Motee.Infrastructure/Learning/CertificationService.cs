using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Learning;
using Motee.Application.Tenancy;
using Motee.Domain.Employees;
using Motee.Domain.Learning;
using Motee.Infrastructure.Common;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Learning;

// The certification register.
//
// A completion certificate's expiry follows the course that issued it and is
// not editable here: on compliance training the expiry is the record, and a
// hand-entered one is a record somebody can get wrong. External credentials
// carry whatever expiry the issuing body gave them.
internal sealed class CertificationService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentEmployee currentEmployee,
    TimeProvider timeProvider) : ICertificationService
{
    private readonly TenantCalendar _calendar = new(dbContext, currentTenant, timeProvider);

    public async Task<PagedResult<CertificationDto>> ListAsync(
        CertificationQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Certification> certifications = Filtered(query);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        // Status is a question about today, so it cannot be asked in SQL
        // without writing the windows twice. The set is one row per credential
        // per person, which is small enough to narrow in memory.
        if (query.Status is CertStatus wanted)
        {
            List<Certification> all = await certifications.ToListAsync(cancellationToken);

            List<Certification> matching =
                [.. all.Where(row => Certifications.StatusOf(row.ExpiresOn, today) == wanted)];

            return new PagedResult<CertificationDto>
            {
                Items = await DescribeAsync(
                    [.. matching.Skip(query.Skip).Take(query.PageSize)], cancellationToken),
                Page = query.Page,
                PageSize = query.PageSize,
                TotalItems = matching.Count,
            };
        }

        int total = await certifications.CountAsync(cancellationToken);

        List<Certification> page = await certifications
            // The ones running out soonest first: that is the list somebody is
            // working through.
            .OrderBy(row => row.ExpiresOn == null)
            .ThenBy(row => row.ExpiresOn)
            .ThenBy(row => row.Title)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<CertificationDto>
        {
            Items = await DescribeAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<CertTally> SummaryAsync(
        CertificationQuery query,
        CancellationToken cancellationToken = default)
    {
        List<DateOnly?> expiries = await Filtered(query)
            .Select(row => row.ExpiresOn)
            .ToListAsync(cancellationToken);

        return Certifications.Tally(
            expiries, await _calendar.TodayAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<CertificationDto>> MineAsync(
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return [];
        }

        List<Certification> mine = await dbContext.Certifications
            .AsNoTracking()
            .Where(row => row.EmployeeId == me)
            .OrderBy(row => row.ExpiresOn == null)
            .ThenBy(row => row.ExpiresOn)
            .ToListAsync(cancellationToken);

        return await DescribeAsync(mine, cancellationToken);
    }

    public async Task<CertificationResult> RecordAsync(
        CertificationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await EmployedAsync(request.EmployeeId, cancellationToken))
        {
            return CertificationResult.Failed(LearningOutcome.EmployeeNotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        Certification certification = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            EmployeeId = request.EmployeeId,

            // Recorded by hand is always an external credential. A completion
            // certificate is issued by finishing the course.
            CertificationKind = CertificationKind.Professional,
            Title = string.Empty,
            CreatedAt = now,
        };

        Apply(certification, request, now);

        LearningRefusal refusal = Refusal(certification);

        if (refusal != LearningRefusal.None)
        {
            return CertificationResult.Refused(refusal);
        }

        dbContext.Certifications.Add(certification);

        await dbContext.SaveChangesAsync(cancellationToken);

        return CertificationResult.Ok(
            (await DescribeAsync([certification], cancellationToken))[0]);
    }

    public async Task<CertificationResult> UpdateAsync(
        Guid id,
        CertificationRequest request,
        CancellationToken cancellationToken = default)
    {
        Certification? certification = await dbContext.Certifications
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (certification is null)
        {
            return CertificationResult.Failed(LearningOutcome.NotFound);
        }

        if (!await EmployedAsync(request.EmployeeId, cancellationToken))
        {
            return CertificationResult.Failed(LearningOutcome.EmployeeNotFound);
        }

        DateOnly? was = certification.ExpiresOn;

        Apply(certification, request, timeProvider.GetUtcNow());

        // A completion certificate's expiry follows its course. Editing the
        // title or the credential number is fine; overwriting the date the
        // course's validity worked out is not.
        if (certification.CertificationKind == CertificationKind.Course)
        {
            certification.ExpiresOn = was;
        }

        LearningRefusal refusal = Refusal(certification);

        if (refusal != LearningRefusal.None)
        {
            return CertificationResult.Refused(refusal);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return CertificationResult.Ok(
            (await DescribeAsync([certification], cancellationToken))[0]);
    }

    public async Task<CertificationResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Certification? certification = await dbContext.Certifications
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (certification is null)
        {
            return CertificationResult.Failed(LearningOutcome.NotFound);
        }

        dbContext.Certifications.Remove(certification);

        await dbContext.SaveChangesAsync(cancellationToken);

        return CertificationResult.Done();
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private IQueryable<Certification> Filtered(CertificationQuery query)
    {
        IQueryable<Certification> certifications =
            dbContext.Certifications.AsNoTracking();

        if (query.EmployeeId is Guid employeeId)
        {
            certifications = certifications.Where(row => row.EmployeeId == employeeId);
        }

        if (query.CourseId is Guid courseId)
        {
            certifications = certifications.Where(row => row.CourseId == courseId);
        }

        if (query.CertificationKind is CertificationKind kind)
        {
            certifications = certifications.Where(row => row.CertificationKind == kind);
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            certifications = certifications.Where(row => row.Category == query.Category);
        }

        if (query.DepartmentId is Guid departmentId)
        {
            certifications = certifications.Where(row => dbContext.Employees.Any(employee =>
                employee.Id == row.EmployeeId && employee.DepartmentId == departmentId));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{Escaped(query.Search.Trim())}%";

            certifications = certifications.Where(row =>
                EF.Functions.ILike(row.Title, pattern, "\\")
                || (row.IssuingBody != null
                    && EF.Functions.ILike(row.IssuingBody, pattern, "\\"))
                || (row.CredentialId != null
                    && EF.Functions.ILike(row.CredentialId, pattern, "\\")));
        }

        return certifications;
    }

    private Task<bool> EmployedAsync(Guid employeeId, CancellationToken cancellationToken) =>
        dbContext.Employees.AnyAsync(
            employee => employee.Id == employeeId
                && employee.Status != EmployeeStatus.Deleted,
            cancellationToken);

    private static void Apply(
        Certification certification,
        CertificationRequest request,
        DateTimeOffset now)
    {
        certification.EmployeeId = request.EmployeeId;
        certification.Title = request.Title?.Trim() ?? string.Empty;
        certification.IssuingBody = Blank(request.IssuingBody);
        certification.Category = Blank(request.Category);
        certification.CredentialId = Blank(request.CredentialId);
        certification.IssuedOn = request.IssuedOn;
        certification.ExpiresOn = request.ExpiresOn;
        certification.StoredFileId = request.StoredFileId;
        certification.UpdatedAt = now;
    }

    private static LearningRefusal Refusal(Certification certification)
    {
        if (string.IsNullOrWhiteSpace(certification.Title))
        {
            return LearningRefusal.CertificateTitleRequired;
        }

        return certification.ExpiresOn is DateOnly expires
            && expires < certification.IssuedOn
            ? LearningRefusal.ExpiresBeforeIssued
            : LearningRefusal.None;
    }

    private async Task<List<CertificationDto>> DescribeAsync(
        IReadOnlyList<Certification> certifications,
        CancellationToken cancellationToken)
    {
        if (certifications.Count == 0)
        {
            return [];
        }

        List<Guid> employeeIds =
            [.. certifications.Select(row => row.EmployeeId).Distinct()];

        List<Guid> courseIds =
        [
            .. certifications
                .Where(row => row.CourseId is not null)
                .Select(row => row.CourseId!.Value)
                .Distinct(),
        ];

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id, department => department.Name, cancellationToken);

        Dictionary<Guid, Person> people = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employeeIds.Contains(employee.Id))
            .Select(employee => new
            {
                employee.Id,
                Name = employee.FirstName + " " + employee.LastName,
                employee.DepartmentId,
            })
            .ToDictionaryAsync(
                row => row.Id,
                row => new Person(
                    row.Name,
                    row.DepartmentId is Guid id ? departments.GetValueOrDefault(id) : null),
                cancellationToken);

        Dictionary<Guid, string> courses = courseIds.Count == 0
            ? []
            : await dbContext.Courses
                .AsNoTracking()
                .Where(course => courseIds.Contains(course.Id))
                .ToDictionaryAsync(
                    course => course.Id, course => course.Title, cancellationToken);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        return
        [
            .. certifications.Select(row => new CertificationDto
            {
                Id = row.Id,
                EmployeeId = row.EmployeeId,
                EmployeeName = people.GetValueOrDefault(row.EmployeeId)?.Name,
                DepartmentName = people.GetValueOrDefault(row.EmployeeId)?.DepartmentName,
                CourseId = row.CourseId,
                CourseTitle = row.CourseId is Guid courseId
                    ? courses.GetValueOrDefault(courseId)
                    : null,
                CertificationKind = row.CertificationKind,
                Title = row.Title,
                IssuingBody = row.IssuingBody,
                Category = row.Category,
                CredentialId = row.CredentialId,
                IssuedOn = row.IssuedOn,
                ExpiresOn = row.ExpiresOn,

                // Derived from the expiry against today, never stored.
                Status = Certifications.StatusOf(row.ExpiresOn, today),
                DaysToExpiry = row.ExpiresOn is DateOnly expiry
                    ? expiry.DayNumber - today.DayNumber
                    : null,
                StoredFileId = row.StoredFileId,

                // So the register withholds the field rather than offering one
                // that is overwritten on save.
                ExpiryIsDerived = row.CertificationKind == CertificationKind.Course,
            }),
        ];
    }

    private static string Escaped(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record Person(string Name, string? DepartmentName);
}
