using Microsoft.EntityFrameworkCore;
using Motee.Application.Leave;
using Motee.Application.Tenancy;
using Motee.Domain.Common;
using Motee.Domain.Leave;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Leave;

internal sealed class LeavePolicyService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : ILeavePolicyService
{
    public async Task<IReadOnlyList<LeaveTypeDto>> ListTypesAsync(
        CancellationToken cancellationToken = default) =>
        await Project(dbContext.LeaveTypes)
            .OrderBy(type => type.Sequence)
            .ThenBy(type => type.Name)
            .ToListAsync(cancellationToken);

    public async Task<LeaveTypeResult> CreateTypeAsync(
        LeaveTypeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Invalid(request.Policy) is LeavePolicyOutcome problem)
        {
            return LeaveTypeResult.Failed(problem);
        }

        string name = request.Name.Trim();

        if (await dbContext.LeaveTypes.AnyAsync(type => type.Name == name, cancellationToken))
        {
            return LeaveTypeResult.Failed(LeavePolicyOutcome.DuplicateName);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        int nextSequence = await dbContext.LeaveTypes.AnyAsync(cancellationToken)
            ? await dbContext.LeaveTypes.MaxAsync(type => type.Sequence, cancellationToken) + 1
            : 0;

        Guid typeId = Guid.NewGuid();

        dbContext.LeaveTypes.Add(new LeaveType
        {
            Id = typeId,

            // No Code: that is reserved for the types we seed, so a tenant renaming
            // "Annual Leave" does not break the seeder's own lookups. A custom type has
            // nothing to be recognised as.
            Name = name,
            IsPaid = request.IsPaid,
            IsActive = request.IsActive,
            Sequence = nextSequence,
            CreatedAt = now,
            UpdatedAt = now,
        });

        dbContext.LeavePolicies.Add(BuildPolicy(typeId, name, request.Policy, now));

        await dbContext.SaveChangesAsync(cancellationToken);

        return LeaveTypeResult.Ok(await GetTypeAsync(typeId, cancellationToken));
    }

    public async Task<LeaveTypeResult> UpdateTypeAsync(
        Guid id,
        LeaveTypeRequest request,
        CancellationToken cancellationToken = default)
    {
        LeaveType? type = await dbContext.LeaveTypes
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (type is null)
        {
            return LeaveTypeResult.Failed(LeavePolicyOutcome.NotFound);
        }

        if (Invalid(request.Policy) is LeavePolicyOutcome problem)
        {
            return LeaveTypeResult.Failed(problem);
        }

        string name = request.Name.Trim();

        bool taken = await dbContext.LeaveTypes
            .AnyAsync(other => other.Id != id && other.Name == name, cancellationToken);

        if (taken)
        {
            return LeaveTypeResult.Failed(LeavePolicyOutcome.DuplicateName);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        type.Name = name;
        type.IsPaid = request.IsPaid;
        type.IsActive = request.IsActive;
        type.UpdatedAt = now;

        LeavePolicy? policy = await dbContext.LeavePolicies
            .FirstOrDefaultAsync(candidate => candidate.LeaveTypeId == id, cancellationToken);

        if (policy is null)
        {
            dbContext.LeavePolicies.Add(BuildPolicy(id, name, request.Policy, now));
        }
        else
        {
            // Edited in place rather than replaced. Requests snapshot their own day count
            // at submission, so nothing already booked is reading these rows — which is
            // what makes changing them safe, and is the reason for the snapshot.
            Apply(policy, request.Policy, now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return LeaveTypeResult.Ok(await GetTypeAsync(id, cancellationToken));
    }

    public async Task<LeavePolicyOutcome> DeactivateTypeAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        LeaveType? type = await dbContext.LeaveTypes
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (type is null)
        {
            return LeavePolicyOutcome.NotFound;
        }

        type.IsActive = false;
        type.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return LeavePolicyOutcome.Succeeded;
    }

    public async Task<IReadOnlyList<LeaveBlackoutDto>> ListBlackoutsAsync(
        CancellationToken cancellationToken = default)
    {
        List<LeaveBlackout> blackouts = await dbContext.LeaveBlackouts
            .AsNoTracking()
            .OrderByDescending(blackout => blackout.StartDate)
            .ToListAsync(cancellationToken);

        return await DescribeAsync(blackouts, cancellationToken);
    }

    public async Task<LeaveBlackoutResult> SaveBlackoutAsync(
        Guid? id,
        LeaveBlackoutRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.EndDate < request.StartDate)
        {
            return LeaveBlackoutResult.Failed(LeavePolicyOutcome.InvalidRule);
        }

        // Refused rather than treated as "everything". A blackout covering every type
        // would include sick leave, and telling somebody they may not be ill until
        // January is not a rule any company means to write.
        List<Guid> typeIds = [.. request.LeaveTypeIds.Distinct()];

        if (typeIds.Count == 0)
        {
            return LeaveBlackoutResult.Failed(LeavePolicyOutcome.InvalidRule);
        }

        // Ids from another company are simply not found, which the tenant filter answers
        // on its own — but a blackout naming a type that does not exist here would sit in
        // the list blocking nothing, looking like it worked.
        int knownTypes = await dbContext.LeaveTypes
            .CountAsync(type => typeIds.Contains(type.Id), cancellationToken);

        if (knownTypes != typeIds.Count)
        {
            return LeaveBlackoutResult.Failed(LeavePolicyOutcome.NotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        LeaveBlackout? blackout = id is Guid existing
            ? await dbContext.LeaveBlackouts
                .FirstOrDefaultAsync(candidate => candidate.Id == existing, cancellationToken)
            : null;

        if (id is not null && blackout is null)
        {
            return LeaveBlackoutResult.Failed(LeavePolicyOutcome.NotFound);
        }

        if (blackout is null)
        {
            blackout = new LeaveBlackout
            {
                Id = Guid.NewGuid(),
                Name = request.Name.Trim(),
                CreatedAt = now,
                UpdatedAt = now,
            };

            dbContext.LeaveBlackouts.Add(blackout);
        }

        blackout.Name = request.Name.Trim();
        blackout.Reason = Trimmed(request.Reason);
        blackout.StartDate = request.StartDate;
        blackout.EndDate = request.EndDate;
        blackout.LeaveTypeIds = typeIds;
        blackout.DepartmentIds = [.. request.DepartmentIds.Distinct()];
        blackout.IsActive = request.IsActive;
        blackout.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return LeaveBlackoutResult.Ok(
            (await DescribeAsync([blackout], cancellationToken))[0]);
    }

    public async Task<LeavePolicyOutcome> RemoveBlackoutAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        LeaveBlackout? blackout = await dbContext.LeaveBlackouts
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (blackout is null)
        {
            return LeavePolicyOutcome.NotFound;
        }

        // Removed outright. A blackout that has passed constrains nothing, and leave
        // already booked snapshotted its own day count — so deleting one cannot change
        // what anybody was previously allowed.
        dbContext.LeaveBlackouts.Remove(blackout);

        await dbContext.SaveChangesAsync(cancellationToken);

        return LeavePolicyOutcome.Succeeded;
    }

    // Names resolved in one pass for the whole list rather than per row, so a settings
    // screen with a dozen blackouts is three queries and not thirty.
    private async Task<IReadOnlyList<LeaveBlackoutDto>> DescribeAsync(
        IReadOnlyList<LeaveBlackout> blackouts,
        CancellationToken cancellationToken)
    {
        if (blackouts.Count == 0)
        {
            return [];
        }

        List<Guid> typeIds = [.. blackouts.SelectMany(b => b.LeaveTypeIds).Distinct()];
        List<Guid> departmentIds = [.. blackouts.SelectMany(b => b.DepartmentIds).Distinct()];

        Dictionary<Guid, string> types = await dbContext.LeaveTypes
            .AsNoTracking()
            .Where(type => typeIds.Contains(type.Id))
            .ToDictionaryAsync(type => type.Id, type => type.Name, cancellationToken);

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .Where(department => departmentIds.Contains(department.Id))
            .ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken);

        return
        [
            .. blackouts.Select(blackout => new LeaveBlackoutDto
            {
                Id = blackout.Id,
                Name = blackout.Name,
                Reason = blackout.Reason,
                StartDate = blackout.StartDate,
                EndDate = blackout.EndDate,
                LeaveTypeIds = blackout.LeaveTypeIds,
                LeaveTypeNames =
                [
                    .. blackout.LeaveTypeIds
                        .Select(id => types.GetValueOrDefault(id))
                        .OfType<string>(),
                ],
                DepartmentIds = blackout.DepartmentIds,
                DepartmentNames =
                [
                    .. blackout.DepartmentIds
                        .Select(id => departments.GetValueOrDefault(id))
                        .OfType<string>(),
                ],
                IsActive = blackout.IsActive,
            }),
        ];
    }

    public async Task<IReadOnlyList<PublicHolidayDto>> ListHolidaysAsync(
        int? year = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<PublicHoliday> holidays = dbContext.PublicHolidays;

        if (year is int wanted)
        {
            holidays = holidays.Where(holiday => holiday.Date.Year == wanted);
        }

        return await holidays
            .AsNoTracking()
            .OrderBy(holiday => holiday.Date)
            .Select(holiday => new PublicHolidayDto
            {
                Id = holiday.Id,
                Date = holiday.Date,
                Name = holiday.Name,
                CountryCode = holiday.CountryCode,
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<PublicHolidayResult> AddHolidayAsync(
        PublicHolidayRequest request,
        CancellationToken cancellationToken = default)
    {
        string? country = Trimmed(request.CountryCode)?.ToUpperInvariant();

        bool exists = await dbContext.PublicHolidays.AnyAsync(
            holiday => holiday.Date == request.Date && holiday.CountryCode == country,
            cancellationToken);

        if (exists)
        {
            return PublicHolidayResult.Failed(LeavePolicyOutcome.DuplicateDate);
        }

        PublicHoliday added = new()
        {
            Id = Guid.NewGuid(),
            Date = request.Date,
            Name = request.Name.Trim(),
            CountryCode = country,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        dbContext.PublicHolidays.Add(added);

        await dbContext.SaveChangesAsync(cancellationToken);

        return PublicHolidayResult.Ok(new PublicHolidayDto
        {
            Id = added.Id,
            Date = added.Date,
            Name = added.Name,
            CountryCode = added.CountryCode,
        });
    }

    public async Task<LeavePolicyOutcome> RemoveHolidayAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        PublicHoliday? holiday = await dbContext.PublicHolidays
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (holiday is null)
        {
            return LeavePolicyOutcome.NotFound;
        }

        // Removed outright rather than deactivated, unlike a leave type. Requests
        // snapshot their day count, so deleting a holiday cannot change what leave
        // already booked cost — and a wrongly entered closure day should simply go.
        dbContext.PublicHolidays.Remove(holiday);

        await dbContext.SaveChangesAsync(cancellationToken);

        return LeavePolicyOutcome.Succeeded;
    }

    public async Task<IReadOnlyList<PublicHolidayDto>> GenerateHolidaysAsync(
        int year,
        CancellationToken cancellationToken = default)
    {
        CountryCode country = await CountryAsync(cancellationToken);

        HashSet<DateOnly> already =
        [
            .. await dbContext.PublicHolidays
                .Where(holiday => holiday.Date.Year == year)
                .Select(holiday => holiday.Date)
                .ToListAsync(cancellationToken),
        ];

        DateTimeOffset now = timeProvider.GetUtcNow();

        foreach ((DateOnly date, string name) in PublicHolidayCalendar.For(country, year))
        {
            // Skipped rather than overwritten: a company that renamed a day, or moved it
            // to match a local announcement, has said something we should not undo.
            if (already.Contains(date))
            {
                continue;
            }

            dbContext.PublicHolidays.Add(new PublicHoliday
            {
                Id = Guid.NewGuid(),
                Date = date,
                Name = name,
                CountryCode = country.Value,
                CreatedAt = now,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return await ListHolidaysAsync(year, cancellationToken);
    }

    private async Task<CountryCode> CountryAsync(CancellationToken cancellationToken)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return CountryCode.Nigeria;
        }

        return await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.CountryCode)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // A carry-over cap above the entitlement lets somebody carry days they never had.
    // Negative days are not a policy anybody means.
    private static LeavePolicyOutcome? Invalid(LeavePolicyRequest policy)
    {
        if (policy.DaysPerYear < 0m || policy.MaxCarryOverDays < 0m)
        {
            return LeavePolicyOutcome.InvalidRule;
        }

        if (policy.MinNoticeDays < 0 || policy.MaxConsecutiveDays < 0)
        {
            return LeavePolicyOutcome.InvalidRule;
        }

        return policy.CarryOverAllowed && policy.MaxCarryOverDays > policy.DaysPerYear
            ? LeavePolicyOutcome.InvalidRule
            : null;
    }

    private static LeavePolicy BuildPolicy(
        Guid typeId,
        string typeName,
        LeavePolicyRequest request,
        DateTimeOffset now)
    {
        LeavePolicy policy = new()
        {
            Id = Guid.NewGuid(),
            LeaveTypeId = typeId,
            Name = $"{typeName} Policy",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        Apply(policy, request, now);

        return policy;
    }

    private static void Apply(LeavePolicy policy, LeavePolicyRequest request, DateTimeOffset now)
    {
        policy.Description = Trimmed(request.Description);
        policy.DaysPerYear = request.DaysPerYear;
        policy.MinNoticeDays = request.MinNoticeDays;
        policy.MaxConsecutiveDays = request.MaxConsecutiveDays;
        policy.ExcludePublicHolidays = request.ExcludePublicHolidays;
        policy.RequiresMedicalCertificate = request.RequiresMedicalCertificate;
        policy.AttachmentRequirement = Trimmed(request.AttachmentRequirement);
        policy.TracksBalance = request.TracksBalance;
        policy.CarryOverAllowed = request.CarryOverAllowed;
        policy.MaxCarryOverDays = request.CarryOverAllowed ? request.MaxCarryOverDays : 0m;
        policy.CarryOverExpiryMonths = request.CarryOverExpiryMonths;
        policy.AccruesMonthly = request.AccruesMonthly;
        policy.Eligibility = Trimmed(request.Eligibility);
        policy.PublicHolidayNote = Trimmed(request.PublicHolidayNote);
        policy.DocumentUrl = Trimmed(request.DocumentUrl);
        policy.UpdatedAt = now;
    }

    private async Task<LeaveTypeDto> GetTypeAsync(Guid id, CancellationToken cancellationToken) =>
        await Project(dbContext.LeaveTypes.Where(type => type.Id == id))
            .FirstAsync(cancellationToken);

    private IQueryable<LeaveTypeDto> Project(IQueryable<LeaveType> types) =>
        types.AsNoTracking().Select(type => new LeaveTypeDto
        {
            Id = type.Id,
            Code = type.Code,
            Name = type.Name,
            IsPaid = type.IsPaid,
            IsActive = type.IsActive,
            Sequence = type.Sequence,
            Policy = dbContext.LeavePolicies
                .Where(policy => policy.LeaveTypeId == type.Id && policy.IsActive)
                .Select(policy => new LeavePolicyDto
                {
                    Id = policy.Id,
                    Description = policy.Description,
                    DaysPerYear = policy.DaysPerYear,
                    MinNoticeDays = policy.MinNoticeDays,
                    MaxConsecutiveDays = policy.MaxConsecutiveDays,
                    ExcludePublicHolidays = policy.ExcludePublicHolidays,
                    RequiresMedicalCertificate = policy.RequiresMedicalCertificate,
                    AttachmentRequirement = policy.AttachmentRequirement,
                    TracksBalance = policy.TracksBalance,
                    CarryOverAllowed = policy.CarryOverAllowed,
                    MaxCarryOverDays = policy.MaxCarryOverDays,
                    CarryOverExpiryMonths = policy.CarryOverExpiryMonths,
                    AccruesMonthly = policy.AccruesMonthly,
                    Eligibility = policy.Eligibility,
                    PublicHolidayNote = policy.PublicHolidayNote,
                    DocumentUrl = policy.DocumentUrl,
                })
                .FirstOrDefault(),
            OpenRequests = dbContext.LeaveRequests
                .Count(request => request.LeaveTypeId == type.Id
                    && request.Status == LeaveRequestStatus.Pending),
        });

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
