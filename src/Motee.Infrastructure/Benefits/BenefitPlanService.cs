using Microsoft.EntityFrameworkCore;
using Motee.Application.Benefits;
using Motee.Application.Common;
using Motee.Application.Tenancy;
using Motee.Domain.Benefits;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Benefits;

// The benefits catalogue.
//
// Entitlement is never stored against a person. Every figure on this page —
// how many employees a plan reaches, who gets nothing automatically, what one
// person is entitled to — is counted from the plans and the roster at the
// moment it is asked for. A stored list per employee would be yesterday's the
// first time somebody widened a plan, and nobody would know which rows were
// stale.
//
// The employment types a plan covers live in jsonb, which Postgres will not
// let a query filter on, so anything that depends on scope is resolved in
// memory against one grouped headcount query rather than one query per plan.
internal sealed class BenefitPlanService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : IBenefitPlanService
{
    private TenantFacts? _tenant;

    // Headcount grouped by employment type, counted once per request. Every
    // plan reaches a type rather than a person, so this answers the whole page
    // without walking the roster per plan.
    private Workforce? _workforce;

    public async Task<BenefitPlanListDto> ListAsync(
        BenefitPlanQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<BenefitPlan> filtered = await FilteredAsync(query, cancellationToken);

        // Before the category filter, because these counts drive the rail that
        // offers the choice — narrowed by it, picking a category would empty
        // every other row and there would be no way back.
        List<BenefitCategoryCount> categoryCounts =
        [
            .. (await filtered
                .GroupBy(plan => plan.Category)
                .Select(group => new { Category = group.Key, Count = group.Count() })
                .ToListAsync(cancellationToken))
                .OrderBy(row => row.Category)
                .Select(row => new BenefitCategoryCount
                {
                    Category = row.Category,
                    Count = row.Count,
                }),
        ];

        if (query.Category is BenefitCategory category)
        {
            filtered = filtered.Where(plan => plan.Category == category);
        }

        int total = await filtered.CountAsync(cancellationToken);

        List<BenefitPlan> page = await filtered
            .OrderBy(plan => plan.Category)
            .ThenBy(plan => plan.Sequence)
            .ThenBy(plan => plan.Name)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new BenefitPlanListDto
        {
            Plans = new PagedResult<BenefitPlanDto>
            {
                Items = await DescribeAsync(page, cancellationToken),
                Page = query.Page,
                PageSize = query.PageSize,
                TotalItems = total,
            },
            CategoryCounts = categoryCounts,
        };
    }

    public async Task<BenefitPlanDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        BenefitPlan? plan = await dbContext.BenefitPlans
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        return plan is null ? null : (await DescribeAsync([plan], cancellationToken))[0];
    }

    public async Task<BenefitSummaryDto> SummaryAsync(CancellationToken cancellationToken = default)
    {
        List<BenefitPlan> plans = await dbContext.BenefitPlans
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        Workforce workforce = await WorkforceAsync(cancellationToken);

        BenefitReachTally reach = BenefitReach.Of(plans, workforce.ByType, workforce.WithoutType);

        List<BenefitPlan> active =
            [.. plans.Where(plan => plan.Status == BenefitPlanStatus.Active)];

        return new BenefitSummaryDto
        {
            Plans = plans.Count,
            Active = active.Count,
            Draft = plans.Count(plan => plan.Status == BenefitPlanStatus.Draft),
            Archived = plans.Count(plan => plan.Status == BenefitPlanStatus.Archived),
            CoreBenefits = active.Count(plan => plan.Enrollment == BenefitEnrollment.Core),
            OptionalBenefits = active.Count(plan => plan.Enrollment == BenefitEnrollment.Optional),
            Employees = reach.Employees,
            EmployeesWithoutEmploymentType = reach.WithoutEmploymentType,
            CoveredEmployees = reach.Covered,
            EmployeesWithoutCoreCover = reach.WithoutCoreCover,
            AveragePlansPerEmployee = reach.AveragePlansPerEmployee,
            ByCategory =
            [
                .. active
                    .GroupBy(plan => plan.Category)
                    .OrderBy(group => group.Key)
                    .Select(group => new BenefitCategoryCount
                    {
                        Category = group.Key,
                        Count = group.Count(),
                    }),
            ],
        };
    }

    public async Task<BenefitPlanResult> CreateAsync(
        BenefitPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        BenefitPlan plan = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            Name = string.Empty,
            Description = string.Empty,

            // After the catalogue, so a company's own additions sort below the
            // plans they started with.
            Sequence = 1000,
            CreatedAt = now,
        };

        BenefitPlanRefusal applied = await ApplyAsync(plan, request, now, cancellationToken);

        if (applied != BenefitPlanRefusal.None)
        {
            return BenefitPlanResult.Refused(applied);
        }

        dbContext.BenefitPlans.Add(plan);

        await dbContext.SaveChangesAsync(cancellationToken);

        return BenefitPlanResult.Ok((await DescribeAsync([plan], cancellationToken))[0]);
    }

    public async Task<BenefitPlanResult> UpdateAsync(
        Guid id,
        BenefitPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        BenefitPlan? plan = await FindAsync(id, cancellationToken);

        if (plan is null)
        {
            return BenefitPlanResult.Failed(BenefitPlanOutcome.NotFound);
        }

        BenefitPlanRefusal applied = await ApplyAsync(
            plan, request, timeProvider.GetUtcNow(), cancellationToken);

        if (applied != BenefitPlanRefusal.None)
        {
            return BenefitPlanResult.Refused(applied);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return BenefitPlanResult.Ok((await DescribeAsync([plan], cancellationToken))[0]);
    }

    public async Task<BenefitPlanResult> DuplicateAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        BenefitPlan? source = await dbContext.BenefitPlans
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (source is null)
        {
            return BenefitPlanResult.Failed(BenefitPlanOutcome.NotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        BenefitPlan copy = new()
        {
            Id = Guid.NewGuid(),
            TenantId = source.TenantId,

            // The copy is the company's own, whatever it was copied from —
            // which is what lets it be deleted where the original could not.
            SeedKey = null,
            Name = Truncated($"{source.Name} (Copy)"),
            Category = source.Category,
            Description = source.Description,
            Provider = source.Provider,
            CoverageLevelId = source.CoverageLevelId,
            CoverageDetails = source.CoverageDetails,
            EmployerContributionPercent = source.EmployerContributionPercent,
            EmployeeContributionPercent = source.EmployeeContributionPercent,
            CostNote = source.CostNote,
            WaitingPeriodDays = source.WaitingPeriodDays,
            Scope = source.Scope,
            EmploymentTypes = [.. source.EmploymentTypes],

            // Always a draft. Duplicating a live plan into a second live plan
            // would double everybody's entitlement on a misclick.
            Status = BenefitPlanStatus.Draft,
            Enrollment = source.Enrollment,
            Country = source.Country,
            Sequence = source.Sequence + 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.BenefitPlans.Add(copy);

        await dbContext.SaveChangesAsync(cancellationToken);

        return BenefitPlanResult.Ok((await DescribeAsync([copy], cancellationToken))[0]);
    }

    public async Task<BenefitPlanResult> SetStatusAsync(
        Guid id,
        BenefitPlanStatus status,
        CancellationToken cancellationToken = default)
    {
        BenefitPlan? plan = await FindAsync(id, cancellationToken);

        if (plan is null)
        {
            return BenefitPlanResult.Failed(BenefitPlanOutcome.NotFound);
        }

        plan.Status = status;
        plan.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return BenefitPlanResult.Ok((await DescribeAsync([plan], cancellationToken))[0]);
    }

    public async Task<BenefitPlanResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        BenefitPlan? plan = await FindAsync(id, cancellationToken);

        if (plan is null)
        {
            return BenefitPlanResult.Failed(BenefitPlanOutcome.NotFound);
        }

        // A catalogue plan is archived instead, so the list stays recognisable
        // across tenants and support can still say what a plan was.
        if (!BenefitPlans.CanDelete(plan))
        {
            return BenefitPlanResult.Refused(BenefitPlanRefusal.CataloguePlanCannotBeDeleted);
        }

        dbContext.BenefitPlans.Remove(plan);

        await dbContext.SaveChangesAsync(cancellationToken);

        return BenefitPlanResult.Done();
    }

    // ── coverage levels ─────────────────────────────────────────────────────

    public async Task<IReadOnlyList<BenefitCoverageLevelDto>> CoverageLevelsAsync(
        CancellationToken cancellationToken = default)
    {
        List<BenefitCoverageLevel> levels = await dbContext.BenefitCoverageLevels
            .AsNoTracking()
            .OrderBy(level => level.Sequence)
            .ThenBy(level => level.Name)
            .ToListAsync(cancellationToken);

        return await DescribeAsync(levels, cancellationToken);
    }

    public async Task<BenefitCoverageLevelResult> AddCoverageLevelAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        string trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return BenefitCoverageLevelResult.Refused(
                BenefitPlanRefusal.CoverageLevelNameRequired);
        }

        if (await LevelNamedAsync(trimmed, null, cancellationToken) is not null)
        {
            return BenefitCoverageLevelResult.Refused(BenefitPlanRefusal.CoverageLevelNameTaken);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        BenefitCoverageLevel level = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            Name = trimmed,
            Sequence = await dbContext.BenefitCoverageLevels.CountAsync(cancellationToken),
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.BenefitCoverageLevels.Add(level);

        await dbContext.SaveChangesAsync(cancellationToken);

        return BenefitCoverageLevelResult.Ok((await DescribeAsync([level], cancellationToken))[0]);
    }

    public async Task<BenefitCoverageLevelResult> RenameCoverageLevelAsync(
        Guid id,
        string name,
        CancellationToken cancellationToken = default)
    {
        BenefitCoverageLevel? level = await dbContext.BenefitCoverageLevels
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (level is null)
        {
            return BenefitCoverageLevelResult.Failed(BenefitPlanOutcome.NotFound);
        }

        string trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return BenefitCoverageLevelResult.Refused(
                BenefitPlanRefusal.CoverageLevelNameRequired);
        }

        if (await LevelNamedAsync(trimmed, id, cancellationToken) is not null)
        {
            return BenefitCoverageLevelResult.Refused(BenefitPlanRefusal.CoverageLevelNameTaken);
        }

        // Nothing to carry across: a plan holds the level's id, not a copy of
        // its label, so every plan on it reads the new name on the next read.
        level.Name = trimmed;
        level.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return BenefitCoverageLevelResult.Ok((await DescribeAsync([level], cancellationToken))[0]);
    }

    public async Task<BenefitPlanOutcome> RemoveCoverageLevelAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        BenefitCoverageLevel? level = await dbContext.BenefitCoverageLevels
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (level is null)
        {
            return BenefitPlanOutcome.NotFound;
        }

        // Cleared here rather than left to the foreign key, so the plans'
        // updated_at moves with the change that altered them.
        List<BenefitPlan> affected = await dbContext.BenefitPlans
            .Where(plan => plan.CoverageLevelId == id)
            .ToListAsync(cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        foreach (BenefitPlan plan in affected)
        {
            plan.CoverageLevelId = null;
            plan.UpdatedAt = now;
        }

        dbContext.BenefitCoverageLevels.Remove(level);

        await dbContext.SaveChangesAsync(cancellationToken);

        return BenefitPlanOutcome.Succeeded;
    }

    // ── one person's entitlements ───────────────────────────────────────────

    public async Task<EmployeeBenefitsDto?> ForEmployeeAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        Holder? holder = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == employeeId
                && employee.Status != EmployeeStatus.Deleted)
            .Select(employee => new Holder(employee.EmploymentType, employee.StartDate))
            .FirstOrDefaultAsync(cancellationToken);

        if (holder is null)
        {
            return null;
        }

        List<BenefitPlan> active = await dbContext.BenefitPlans
            .AsNoTracking()
            .Where(plan => plan.Status == BenefitPlanStatus.Active)
            .OrderBy(plan => plan.Category)
            .ThenBy(plan => plan.Sequence)
            .ThenBy(plan => plan.Name)
            .ToListAsync(cancellationToken);

        // Scope lives in jsonb, so this is the one filter Postgres cannot do.
        List<BenefitPlan> entitled =
            [.. active.Where(plan => BenefitPlans.IsEligible(plan, holder.EmploymentType))];

        TenantFacts tenant = await TenantAsync(cancellationToken);

        DateOnly today = TenantClock.DayOf(timeProvider.GetUtcNow(), tenant.TimeZoneId);

        Dictionary<Guid, string> labels = await LabelsAsync(entitled, cancellationToken);

        Workforce workforce = await WorkforceAsync(cancellationToken);

        List<EmployeeBenefitDto> described =
        [
            .. entitled.Select(plan => new EmployeeBenefitDto
            {
                Plan = Describe(plan, labels, workforce),
                State = Entitlements.StateOf(plan, holder.StartDate, today),
                EffectiveFrom = BenefitPlans.EffectiveFrom(plan, holder.StartDate),
            }),
        ];

        return new EmployeeBenefitsDto
        {
            EmployeeId = employeeId,
            EmploymentType = holder.EmploymentType,
            Core = [.. described.Where(row => row.Plan.Enrollment == BenefitEnrollment.Core)],
            Optional =
                [.. described.Where(row => row.Plan.Enrollment == BenefitEnrollment.Optional)],
        };
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private Task<BenefitPlan?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.BenefitPlans.FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

    private async Task<IQueryable<BenefitPlan>> FilteredAsync(
        BenefitPlanQuery query,
        CancellationToken cancellationToken)
    {
        IQueryable<BenefitPlan> plans = dbContext.BenefitPlans.AsNoTracking();

        if (query.Status is BenefitPlanStatus status)
        {
            plans = plans.Where(plan => plan.Status == status);
        }

        if (query.Enrollment is BenefitEnrollment enrollment)
        {
            plans = plans.Where(plan => plan.Enrollment == enrollment);
        }

        if (query.CoverageLevelId is Guid levelId)
        {
            plans = plans.Where(plan => plan.CoverageLevelId == levelId);
        }
        else if (query.WithoutCoverageLevel == true)
        {
            plans = plans.Where(plan => plan.CoverageLevelId == null);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{Escaped(query.Search.Trim())}%";

            // The coverage label is on the level, not the plan, so the levels
            // whose name matches are resolved first and the plans on them
            // included. One extra read of a handful of rows, against a join
            // that would otherwise run on every filter.
            List<Guid> matchedLevels = await dbContext.BenefitCoverageLevels
                .AsNoTracking()
                .Where(level => EF.Functions.ILike(level.Name, pattern, "\\"))
                .Select(level => level.Id)
                .ToListAsync(cancellationToken);

            plans = plans.Where(plan =>
                EF.Functions.ILike(plan.Name, pattern, "\\")
                || EF.Functions.ILike(plan.Description, pattern, "\\")
                || (plan.Provider != null && EF.Functions.ILike(plan.Provider, pattern, "\\"))
                || (plan.CoverageDetails != null
                    && EF.Functions.ILike(plan.CoverageDetails, pattern, "\\"))
                || (plan.CoverageLevelId != null
                    && matchedLevels.Contains(plan.CoverageLevelId.Value)));
        }

        return plans;
    }

    // A plan legitimately named "Contract (100%)" would otherwise match every
    // other plan in the catalogue.
    private static string Escaped(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private async Task<BenefitPlanRefusal> ApplyAsync(
        BenefitPlan plan,
        BenefitPlanRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Guid? coverageLevelId = null;

        if (!string.IsNullOrWhiteSpace(request.Coverage))
        {
            BenefitCoverageLevel? level = await LevelNamedAsync(
                request.Coverage.Trim(), null, cancellationToken);

            if (level is null)
            {
                return BenefitPlanRefusal.CoverageLevelUnknown;
            }

            coverageLevelId = level.Id;
        }

        CountryCode? country = null;

        if (!string.IsNullOrWhiteSpace(request.Country))
        {
            if (!CountryCode.TryParse(request.Country, out CountryCode parsed))
            {
                return BenefitPlanRefusal.CountryNotThisTenants;
            }

            // The tenant is the country. A plan filed under another one would
            // be invisible on every screen that reads this catalogue, which is
            // a worse outcome than refusing it.
            if (parsed != (await TenantAsync(cancellationToken)).Country)
            {
                return BenefitPlanRefusal.CountryNotThisTenants;
            }

            country = parsed;
        }

        plan.Name = request.Name?.Trim() ?? string.Empty;
        plan.Category = request.Category;
        plan.Description = request.Description?.Trim() ?? string.Empty;
        plan.Provider = Blank(request.Provider);
        plan.CoverageLevelId = coverageLevelId;
        plan.CoverageDetails = Blank(request.CoverageDetails);
        plan.EmployerContributionPercent = request.EmployerContributionPercent;
        plan.EmployeeContributionPercent = request.EmployeeContributionPercent;
        plan.CostNote = Blank(request.CostNote);
        plan.WaitingPeriodDays = request.WaitingPeriodDays;
        plan.Scope = request.Scope;

        // Replaced rather than mutated: the jsonb converter compares by
        // serialising, so an in-place edit is not seen as a change. Distinct,
        // because the same type twice would count a plan's reach twice.
        plan.EmploymentTypes = request.Scope == BenefitScope.EmploymentTypes
            ? [.. (request.EmploymentTypes ?? []).Distinct()]
            : [];

        plan.Status = request.Status;
        plan.Enrollment = request.Enrollment;
        plan.Country = country;
        plan.UpdatedAt = now;

        return BenefitPlans.Refusal(plan);
    }

    private async Task<BenefitCoverageLevel?> LevelNamedAsync(
        string name,
        Guid? ignoring,
        CancellationToken cancellationToken)
    {
        string pattern = Escaped(name);

        // ILike rather than a lowered comparison: it is what Postgres can
        // index, and EF cannot translate a StringComparison.
        return await dbContext.BenefitCoverageLevels
            .AsNoTracking()
            .FirstOrDefaultAsync(
                level => level.Id != ignoring
                    && EF.Functions.ILike(level.Name, pattern, "\\"),
                cancellationToken);
    }

    private async Task<List<BenefitPlanDto>> DescribeAsync(
        IReadOnlyList<BenefitPlan> plans,
        CancellationToken cancellationToken)
    {
        if (plans.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, string> labels = await LabelsAsync(plans, cancellationToken);

        Workforce workforce = await WorkforceAsync(cancellationToken);

        return [.. plans.Select(plan => Describe(plan, labels, workforce))];
    }

    private static BenefitPlanDto Describe(
        BenefitPlan plan,
        IReadOnlyDictionary<Guid, string> labels,
        Workforce workforce) =>
        new()
        {
            Id = plan.Id,
            SeedKey = plan.SeedKey,
            Name = plan.Name,
            Category = plan.Category,
            Description = plan.Description,
            Provider = plan.Provider,
            CoverageLevelId = plan.CoverageLevelId,
            Coverage = plan.CoverageLevelId is Guid levelId
                ? labels.GetValueOrDefault(levelId)
                : null,
            CoverageDetails = plan.CoverageDetails,
            EmployerContributionPercent = plan.EmployerContributionPercent,
            EmployeeContributionPercent = plan.EmployeeContributionPercent,
            CostNote = plan.CostNote,
            WaitingPeriodDays = plan.WaitingPeriodDays,
            Scope = plan.Scope,
            EmploymentTypes = plan.EmploymentTypes,
            Status = plan.Status,
            Enrollment = plan.Enrollment,
            Country = plan.Country?.Value,
            CanDelete = BenefitPlans.CanDelete(plan),

            // Zero on anything that is not live, because a draft is not an
            // entitlement and reporting a reach for one invites somebody to
            // read the catalogue as the cover people have.
            EligibleEmployees = plan.Status == BenefitPlanStatus.Active
                ? workforce.ByType
                    .Where(pair => BenefitPlans.CoversType(plan, pair.Key))
                    .Sum(pair => pair.Value)
                : 0,
            UpdatedAt = plan.UpdatedAt,
        };

    private async Task<List<BenefitCoverageLevelDto>> DescribeAsync(
        IReadOnlyList<BenefitCoverageLevel> levels,
        CancellationToken cancellationToken)
    {
        if (levels.Count == 0)
        {
            return [];
        }

        // One grouped count rather than one query per level.
        Dictionary<Guid, int> counts = await dbContext.BenefitPlans
            .AsNoTracking()
            .Where(plan => plan.CoverageLevelId != null)
            .GroupBy(plan => plan.CoverageLevelId!.Value)
            .Select(group => new { LevelId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.LevelId, row => row.Count, cancellationToken);

        return
        [
            .. levels.Select(level => new BenefitCoverageLevelDto
            {
                Id = level.Id,
                Name = level.Name,
                Sequence = level.Sequence,
                PlanCount = counts.GetValueOrDefault(level.Id),
            }),
        ];
    }

    private async Task<Dictionary<Guid, string>> LabelsAsync(
        IReadOnlyList<BenefitPlan> plans,
        CancellationToken cancellationToken)
    {
        List<Guid> levelIds =
        [
            .. plans
                .Where(plan => plan.CoverageLevelId is not null)
                .Select(plan => plan.CoverageLevelId!.Value)
                .Distinct(),
        ];

        if (levelIds.Count == 0)
        {
            return [];
        }

        return await dbContext.BenefitCoverageLevels
            .AsNoTracking()
            .Where(level => levelIds.Contains(level.Id))
            .ToDictionaryAsync(level => level.Id, level => level.Name, cancellationToken);
    }

    private async Task<Workforce> WorkforceAsync(CancellationToken cancellationToken)
    {
        if (_workforce is not null)
        {
            return _workforce;
        }

        IQueryable<Employee> inPost = dbContext.Employees
            .AsNoTracking()
            .Where(employee => EmployeeLifecycle.InPost.Contains(employee.Status));

        Dictionary<EmploymentType, int> byType = await inPost
            .Where(employee => employee.EmploymentType != null)
            .GroupBy(employee => employee.EmploymentType!.Value)
            .Select(group => new { Type = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Type, row => row.Count, cancellationToken);

        // Reported rather than folded in. The frontend falls back to full-time
        // for an unset type, which hands these people a package nobody decided
        // they get — and moves the average while it does so.
        int withoutType = await inPost
            .CountAsync(employee => employee.EmploymentType == null, cancellationToken);

        _workforce = new Workforce(byType, withoutType);

        return _workforce;
    }

    // Scoped by id, because tenants are not themselves tenant-scoped — an
    // unfiltered read returns some arbitrary company's row and would file this
    // company's plans under another country.
    private async Task<TenantFacts> TenantAsync(CancellationToken cancellationToken)
    {
        if (_tenant is not null)
        {
            return _tenant;
        }

        if (currentTenant.TenantId is not Guid tenantId)
        {
            return new TenantFacts(CountryCode.Nigeria, null);
        }

        Tenant? tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == tenantId, cancellationToken);

        _tenant = new TenantFacts(
            tenant?.CountryCode ?? CountryCode.Nigeria,
            tenant?.Settings?.TimeZoneId);

        return _tenant;
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // A copy of a plan already at the limit would otherwise be refused by its
    // own validation the moment "(Copy)" is added.
    private static string Truncated(string name) =>
        name.Length <= BenefitPlans.LongestName
            ? name
            : name[..BenefitPlans.LongestName];

    private sealed record Holder(EmploymentType? EmploymentType, DateOnly? StartDate);

    private sealed record Workforce(
        IReadOnlyDictionary<EmploymentType, int> ByType,
        int WithoutType);

    private sealed record TenantFacts(CountryCode Country, string? TimeZoneId);
}
