using Microsoft.EntityFrameworkCore;
using Motee.Application.Organisation;
using Motee.Application.Tenancy;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Recruitment;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Organisation;

// The establishment.
//
// Three numbers on every post are derived — who holds it, what is being
// recruited into it, and therefore where it stands. The establishment is what
// finance and HR argue from, so a stored status that said "vacant" over a
// post somebody filled last month would be worse than no status at all.
internal sealed class PositionService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : IPositionService
{
    private static readonly EmployeeStatus[] InPost =
    [
        EmployeeStatus.Probation,
        EmployeeStatus.Active,
        EmployeeStatus.OnLeave,
        EmployeeStatus.Offboarding,
    ];

    public async Task<IReadOnlyList<PositionDto>> ListAsync(
        PositionQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Position> rows = dbContext.Positions.AsNoTracking();

        if (!query.IncludeInactive)
        {
            rows = rows.Where(position => position.IsActive);
        }

        if (query.DepartmentId is Guid departmentId)
        {
            rows = rows.Where(position => position.DepartmentId == departmentId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string needle = $"%{query.Search.Trim()}%";

            rows = rows.Where(position =>
                EF.Functions.ILike(position.Title, needle)
                || (position.Grade != null && EF.Functions.ILike(position.Grade, needle)));
        }

        List<Position> found = await rows
            .OrderBy(position => position.Sequence)
            .ThenBy(position => position.Title)
            .ToListAsync(cancellationToken);

        List<PositionDto> described = await DescribeAsync(found, cancellationToken);

        // Status is derived, so it filters after the describe. Storing it to
        // make this a SQL predicate is the drift the derivation prevents.
        return query.Status is PositionStatus status
            ? [.. described.Where(position => position.Status == status)]
            : described;
    }

    public async Task<PositionDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Position? position = await dbContext.Positions
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        return position is null ? null : (await DescribeAsync([position], cancellationToken))[0];
    }

    public async Task<PositionSummaryDto> SummaryAsync(
        CancellationToken cancellationToken = default)
    {
        List<PositionDto> all = [.. await ListAsync(new PositionQuery(), cancellationToken)];
        List<PositionDto> live = [.. all.Where(position => position.IsActive)];

        int unassigned = await dbContext.Employees.CountAsync(
            employee => employee.PositionId == null && InPost.Contains(employee.Status),
            cancellationToken);

        return new PositionSummaryDto
        {
            Positions = live.Count,
            Establishment = live.Sum(position => position.Headcount),
            Filled = live.Sum(position => position.Assigned),
            OpeningsLeft = live.Sum(position => position.OpeningsLeft),
            Recruiting = live.Count(position => position.Status == PositionStatus.Recruiting),
            Vacant = live.Count(position => position.Status == PositionStatus.Vacant),
            Retired = all.Count(position => !position.IsActive),
            UnassignedEmployees = unassigned,
        };
    }

    public async Task<PositionResult> CreateAsync(
        PositionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await MissingDepartmentAsync(request.DepartmentId, cancellationToken))
        {
            return PositionResult.Failed(PositionOutcome.DepartmentNotFound);
        }

        if (await TitleTakenAsync(request.Title, request.DepartmentId, null, cancellationToken))
        {
            return PositionResult.Refused(PositionRefusal.TitleTakenInDepartment);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        Position position = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            Title = string.Empty,
            Sequence = await dbContext.Positions.CountAsync(cancellationToken),
            CreatedAt = now,
        };

        Apply(position, request, now);

        PositionRefusal refusal = Positions.Refusal(position);

        if (refusal != PositionRefusal.None)
        {
            return PositionResult.Refused(refusal);
        }

        dbContext.Positions.Add(position);

        await dbContext.SaveChangesAsync(cancellationToken);

        return PositionResult.Ok((await DescribeAsync([position], cancellationToken))[0]);
    }

    public async Task<PositionResult> UpdateAsync(
        Guid id,
        PositionRequest request,
        CancellationToken cancellationToken = default)
    {
        Position? position = await FindAsync(id, cancellationToken);

        if (position is null)
        {
            return PositionResult.Failed(PositionOutcome.NotFound);
        }

        if (await MissingDepartmentAsync(request.DepartmentId, cancellationToken))
        {
            return PositionResult.Failed(PositionOutcome.DepartmentNotFound);
        }

        if (await TitleTakenAsync(request.Title, request.DepartmentId, id, cancellationToken))
        {
            return PositionResult.Refused(PositionRefusal.TitleTakenInDepartment);
        }

        Apply(position, request, timeProvider.GetUtcNow());

        PositionRefusal refusal = Positions.Refusal(position);

        if (refusal != PositionRefusal.None)
        {
            return PositionResult.Refused(refusal);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return PositionResult.Ok((await DescribeAsync([position], cancellationToken))[0]);
    }

    public async Task<PositionResult> AssignAsync(
        Guid id,
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        Position? position = await FindAsync(id, cancellationToken);

        if (position is null)
        {
            return PositionResult.Failed(PositionOutcome.NotFound);
        }

        Employee? employee = await dbContext.Employees.FirstOrDefaultAsync(
            row => row.Id == employeeId && row.Status != EmployeeStatus.Deleted,
            cancellationToken);

        if (employee is null)
        {
            return PositionResult.Failed(PositionOutcome.EmployeeNotFound);
        }

        // Deliberately not refused when the post is already at complement.
        // Handover overlaps are real, and a system that blocks them makes
        // somebody record a lie to get the work done.
        employee.PositionId = position.Id;
        employee.UpdatedAt = timeProvider.GetUtcNow();
        position.UpdatedAt = employee.UpdatedAt;

        await dbContext.SaveChangesAsync(cancellationToken);

        return PositionResult.Ok((await DescribeAsync([position], cancellationToken))[0]);
    }

    public async Task<PositionResult> UnassignAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        Employee? employee = await dbContext.Employees.FirstOrDefaultAsync(
            row => row.Id == employeeId, cancellationToken);

        if (employee is null)
        {
            return PositionResult.Failed(PositionOutcome.EmployeeNotFound);
        }

        Guid? was = employee.PositionId;

        employee.PositionId = null;
        employee.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        // Nothing to describe when they were in no post to begin with, which
        // is still the outcome the caller asked for.
        if (was is not Guid previous
            || await FindAsync(previous, cancellationToken) is not Position position)
        {
            return PositionResult.Done();
        }

        return PositionResult.Ok((await DescribeAsync([position], cancellationToken))[0]);
    }

    public async Task<PositionResult> RetireAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Position? position = await FindAsync(id, cancellationToken);

        if (position is null)
        {
            return PositionResult.Failed(PositionOutcome.NotFound);
        }

        // Retiring a post somebody is in would leave their record pointing at
        // something the company says no longer exists. Move them first.
        if (await AssignedCountAsync(id, cancellationToken) > 0)
        {
            return PositionResult.Refused(PositionRefusal.InUse);
        }

        position.IsActive = false;
        position.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return PositionResult.Ok((await DescribeAsync([position], cancellationToken))[0]);
    }

    public async Task<PositionResult> RestoreAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Position? position = await FindAsync(id, cancellationToken);

        if (position is null)
        {
            return PositionResult.Failed(PositionOutcome.NotFound);
        }

        position.IsActive = true;
        position.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return PositionResult.Ok((await DescribeAsync([position], cancellationToken))[0]);
    }

    public async Task<PositionOutcome> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Position? position = await FindAsync(id, cancellationToken);

        if (position is null)
        {
            return PositionOutcome.NotFound;
        }

        if (await AssignedCountAsync(id, cancellationToken) > 0)
        {
            return PositionOutcome.Refused;
        }

        dbContext.Positions.Remove(position);

        await dbContext.SaveChangesAsync(cancellationToken);

        return PositionOutcome.Succeeded;
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private Task<Position?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Positions.FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

    private Task<int> AssignedCountAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Employees.CountAsync(
            employee => employee.PositionId == id && InPost.Contains(employee.Status),
            cancellationToken);

    private async Task<bool> MissingDepartmentAsync(
        Guid? departmentId,
        CancellationToken cancellationToken) =>
        departmentId is Guid id
        && !await dbContext.Departments.AnyAsync(
            department => department.Id == id, cancellationToken);

    // One title per department. Two posts with one title in one department is
    // two answers to "which is Sarah in"; the same title in two departments
    // is ordinary.
    private async Task<bool> TitleTakenAsync(
        string title,
        Guid? departmentId,
        Guid? ignoring,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        string pattern = title.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

        return await dbContext.Positions.AnyAsync(
            row => row.Id != ignoring
                && row.DepartmentId == departmentId
                && EF.Functions.ILike(row.Title, pattern, "\\"),
            cancellationToken);
    }

    private static void Apply(Position position, PositionRequest request, DateTimeOffset now)
    {
        position.Title = request.Title?.Trim() ?? string.Empty;
        position.DepartmentId = request.DepartmentId;
        position.Grade = Blank(request.Grade);
        position.Description = Blank(request.Description);
        position.Headcount = request.Headcount;
        position.UpdatedAt = now;
    }

    private async Task<List<PositionDto>> DescribeAsync(
        IReadOnlyList<Position> positions,
        CancellationToken cancellationToken)
    {
        if (positions.Count == 0)
        {
            return [];
        }

        List<Guid> ids = [.. positions.Select(position => position.Id)];

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id, department => department.Name, cancellationToken);

        List<Holder> holders = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.PositionId != null
                && ids.Contains(employee.PositionId.Value)
                && InPost.Contains(employee.Status))
            .Select(employee => new Holder(
                employee.PositionId!.Value,
                employee.Id,
                employee.FirstName + " " + employee.LastName,
                employee.JobTitle))
            .ToListAsync(cancellationToken);

        // Live vacancies recruiting into each post. Matched on the post's own
        // id where a requisition carries one, which it does once a vacancy is
        // raised from the establishment rather than typed free-hand.
        Dictionary<Guid, int> recruiting = await dbContext.JobRequisitions
            .AsNoTracking()
            .Where(requisition => requisition.PositionId != null
                && ids.Contains(requisition.PositionId.Value)
                && (requisition.Status == RequisitionStatus.Open
                    || requisition.Status == RequisitionStatus.Interviewing
                    || requisition.Status == RequisitionStatus.OfferStage))
            .GroupBy(requisition => requisition.PositionId!.Value)
            .Select(group => new { Id = group.Key, Count = group.Sum(r => r.Openings) })
            .ToDictionaryAsync(row => row.Id, row => row.Count, cancellationToken);

        return
        [
            .. positions.Select(position =>
            {
                List<Holder> theirs =
                    [.. holders.Where(holder => holder.PositionId == position.Id)];

                int open = recruiting.GetValueOrDefault(position.Id);

                return new PositionDto
                {
                    Id = position.Id,
                    Title = position.Title,
                    DepartmentId = position.DepartmentId,
                    DepartmentName = position.DepartmentId is Guid id
                        ? departments.GetValueOrDefault(id)
                        : null,
                    Grade = position.Grade,
                    Description = position.Description,
                    Headcount = position.Headcount,
                    Assigned = theirs.Count,
                    OpeningsLeft = Positions.OpeningsLeft(position.Headcount, theirs.Count),
                    Recruiting = open,
                    Status = Positions.Status(
                        position.IsActive, theirs.Count, open, position.Headcount),
                    IsActive = position.IsActive,
                    Holders =
                    [
                        .. theirs
                            .OrderBy(holder => holder.Name, StringComparer.OrdinalIgnoreCase)
                            .Select(holder => new PositionHolderDto
                            {
                                EmployeeId = holder.EmployeeId,
                                Name = holder.Name,
                                JobTitle = holder.JobTitle,
                            }),
                    ],
                    UpdatedAt = position.UpdatedAt,
                };
            }),
        ];
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record Holder(Guid PositionId, Guid EmployeeId, string Name, string? JobTitle);
}
