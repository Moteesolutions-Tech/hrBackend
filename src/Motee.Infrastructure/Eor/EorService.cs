using Microsoft.EntityFrameworkCore;
using Motee.Application.Audit;
using Motee.Application.Common;
using Motee.Application.Eor;
using Motee.Application.Tenancy;
using Motee.Domain.Audit;
using Motee.Domain.Employees;
using Motee.Domain.Eor;
using Motee.Infrastructure.Common;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Eor;

// Providers and the workers engaged through them.
//
// An engagement against an existing employee, never a worker record of its own:
// employees already holds every personal and contact column the EOR screen
// collects, and a second table would be a second people register with nothing
// keeping the two in step.
internal sealed class EorService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    IAuditTrail audit,
    TimeProvider timeProvider) : IEorService
{
    private const string Module = "organization.eor";

    private readonly TenantCalendar _calendar = new(dbContext, currentTenant, timeProvider);

    // ── providers ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<EorProviderDto>> ProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        List<EorProvider> providers = await dbContext.EorProviders
            .AsNoTracking()
            .OrderBy(provider => provider.Name)
            .ToListAsync(cancellationToken);

        List<EorEngagement> engagements = await dbContext.EorEngagements
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return [.. providers.Select(provider => Describe(provider, engagements, today))];
    }

    public async Task<EorProviderDto?> ProviderAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        EorProvider? provider = await dbContext.EorProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (provider is null)
        {
            return null;
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        List<EorEngagement> engagements = await dbContext.EorEngagements
            .AsNoTracking()
            .Where(engagement => engagement.ProviderId == id)
            .ToListAsync(cancellationToken);

        return Describe(provider, engagements, today);
    }

    public async Task<EorResult<EorProviderDto>> AddProviderAsync(
        EorProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        List<string> countries = EorProviders.Tidy(request.CountriesCovered);

        EorRefusal refusal = Domain.Eor.EorProviders.Check(
            request.Name, request.ManagementFeePercent, countries);

        if (refusal != EorRefusal.None)
        {
            return EorResult<EorProviderDto>.Refused(refusal);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        EorProvider provider = new()
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Status = request.Status,
            CountriesCovered = countries,
            ManagementFeePercent = request.ManagementFeePercent,
            PartneredOn = request.PartneredOn,
            Website = Trimmed(request.Website),
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.EorProviders.Add(provider);

        audit.Record(
            AuditAction.Create,
            Module,
            $"Added EOR provider {provider.Name}",
            entityId: provider.Id,
            entityType: nameof(EorProvider));

        await dbContext.SaveChangesAsync(cancellationToken);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        return EorResult<EorProviderDto>.Ok(Describe(provider, [], today));
    }

    public async Task<EorResult<EorProviderDto>> UpdateProviderAsync(
        Guid id,
        EorProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        EorProvider? provider = await dbContext.EorProviders
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (provider is null)
        {
            return EorResult<EorProviderDto>.Failed(EorOutcome.NotFound);
        }

        List<string> countries = EorProviders.Tidy(request.CountriesCovered);

        EorRefusal refusal = Domain.Eor.EorProviders.Check(
            request.Name, request.ManagementFeePercent, countries);

        if (refusal != EorRefusal.None)
        {
            return EorResult<EorProviderDto>.Refused(refusal);
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        List<EorEngagement> engagements = await dbContext.EorEngagements
            .AsNoTracking()
            .Where(engagement => engagement.ProviderId == id)
            .ToListAsync(cancellationToken);

        // Dropping a country somebody is currently engaged in would leave that
        // worker with no legal employer and nothing on the screen saying so —
        // the same hole the coverage check closes on the way in.
        List<EorEngagement> live =
            [.. engagements.Where(engagement => Engagements.IsLive(engagement, today))];

        bool strands = live.Any(engagement =>
            !countries.Any(country => string.Equals(
                country, engagement.CountryOfEngagement.Trim(), StringComparison.OrdinalIgnoreCase)));

        if (strands)
        {
            return EorResult<EorProviderDto>.Refused(EorRefusal.ProviderDoesNotCoverCountry);
        }

        // Retiring through the update path is refused for the same reason the
        // dedicated route refuses it: workers attached to a partner the company
        // has stopped using.
        if (request.Status == EorProviderStatus.Inactive && live.Count > 0)
        {
            return EorResult<EorProviderDto>.Refused(EorRefusal.ProviderHasLiveEngagements);
        }

        provider.Name = request.Name.Trim();
        provider.Status = request.Status;

        // Replaced rather than modified: the column's comparer serialises, so
        // changing the list in place leaves EF seeing no change.
        provider.CountriesCovered = countries;

        provider.ManagementFeePercent = request.ManagementFeePercent;
        provider.PartneredOn = request.PartneredOn;
        provider.Website = Trimmed(request.Website);
        provider.UpdatedAt = timeProvider.GetUtcNow();

        audit.Record(
            AuditAction.Update,
            Module,
            $"Updated EOR provider {provider.Name}",
            entityId: provider.Id,
            entityType: nameof(EorProvider));

        await dbContext.SaveChangesAsync(cancellationToken);

        return EorResult<EorProviderDto>.Ok(Describe(provider, engagements, today));
    }

    public async Task<EorResult<EorProviderDto>> RetireProviderAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        EorProvider? provider = await dbContext.EorProviders
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (provider is null)
        {
            return EorResult<EorProviderDto>.Failed(EorOutcome.NotFound);
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        List<EorEngagement> engagements = await dbContext.EorEngagements
            .AsNoTracking()
            .Where(engagement => engagement.ProviderId == id)
            .ToListAsync(cancellationToken);

        if (engagements.Any(engagement => Engagements.IsLive(engagement, today)))
        {
            return EorResult<EorProviderDto>.Refused(EorRefusal.ProviderHasLiveEngagements);
        }

        // Made inactive rather than deleted. Invoices and ended engagements
        // point at it, and the record of who employed somebody three years ago
        // is the point of keeping any of this.
        provider.Status = EorProviderStatus.Inactive;
        provider.UpdatedAt = timeProvider.GetUtcNow();

        audit.Record(
            AuditAction.Update,
            Module,
            $"Retired EOR provider {provider.Name}",
            entityId: provider.Id,
            entityType: nameof(EorProvider));

        await dbContext.SaveChangesAsync(cancellationToken);

        return EorResult<EorProviderDto>.Ok(Describe(provider, engagements, today));
    }

    // ── workers ────────────────────────────────────────────────────────────

    public async Task<PagedResult<EorWorkerDto>> WorkersAsync(
        EorWorkerQuery query,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        IQueryable<EorEngagement> source = dbContext.EorEngagements.AsNoTracking();

        if (query.ProviderId is Guid providerId)
        {
            source = source.Where(engagement => engagement.ProviderId == providerId);
        }

        if (!string.IsNullOrWhiteSpace(query.Country))
        {
            string country = query.Country.Trim();

            source = source.Where(engagement => engagement.CountryOfEngagement == country);
        }

        List<EorEngagement> engagements = await source.ToListAsync(cancellationToken);

        List<EorWorkerDto> described = await DescribeAsync(engagements, today, cancellationToken);

        // Stage and blockers are questions about today, so neither can be asked
        // in SQL without writing the rules twice. The set is one row per
        // engaged worker, which is small enough to narrow in memory.
        if (query.Stage is EngagementStage stage)
        {
            described = [.. described.Where(worker => worker.Stage == stage)];
        }

        if (query.WithBlockers is bool wanted)
        {
            described = [.. described.Where(worker => worker.Blockers.Count > 0 == wanted)];
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string needle = query.Search.Trim();

            described =
            [
                .. described.Where(worker =>
                    worker.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                        || (worker.Email?.Contains(needle, StringComparison.OrdinalIgnoreCase)
                            ?? false)),
            ];
        }

        // The ones needing attention first: that is the list somebody is
        // working through.
        described =
        [
            .. described
                .OrderByDescending(worker => worker.Blockers.Count)
                .ThenBy(worker => worker.Name, StringComparer.OrdinalIgnoreCase),
        ];

        return new PagedResult<EorWorkerDto>
        {
            Items = [.. described.Skip(query.Skip).Take(query.PageSize)],
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = described.Count,
        };
    }

    public async Task<EorWorkerDto?> WorkerAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        EorEngagement? engagement = await dbContext.EorEngagements
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (engagement is null)
        {
            return null;
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        return (await DescribeAsync([engagement], today, cancellationToken))[0];
    }

    public async Task<EorResult<EorWorkerDto>> EngageAsync(
        EorEngagementRequest request,
        CancellationToken cancellationToken = default)
    {
        Employee? employee = await dbContext.Employees
            .FirstOrDefaultAsync(row => row.Id == request.EmployeeId, cancellationToken);

        if (employee is null)
        {
            return EorResult<EorWorkerDto>.Refused(EorRefusal.EmployeeNotFound);
        }

        EorProvider? provider = await dbContext.EorProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == request.ProviderId, cancellationToken);

        if (provider is null)
        {
            return EorResult<EorWorkerDto>.Refused(EorRefusal.ProviderNotFound);
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();

        bool alreadyEngaged = await dbContext.EorEngagements
            .AsNoTracking()
            .Where(engagement => engagement.EmployeeId == request.EmployeeId)
            .AnyAsync(engagement => engagement.EndDate == null, cancellationToken);

        EorEngagement engagement = new()
        {
            Id = Guid.NewGuid(),
            EmployeeId = request.EmployeeId,
            ProviderId = request.ProviderId,
            CountryOfEngagement = (request.CountryOfEngagement ?? string.Empty).Trim(),
            Currency = (request.Currency ?? string.Empty).Trim().ToUpperInvariant(),
            GrossMonthly = request.GrossMonthly,
            EmployerCostMonthly = request.EmployerCostMonthly,
            PayFrequency = request.PayFrequency,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            UsdRate = request.UsdRate,
            RateOn = request.RateOn,
            CreatedAt = now,
            UpdatedAt = now,
        };

        EorRefusal refusal = Engagements.Check(engagement, provider, alreadyEngaged);

        if (refusal != EorRefusal.None)
        {
            return EorResult<EorWorkerDto>.Refused(refusal);
        }

        dbContext.EorEngagements.Add(engagement);

        // Everything outstanding. The screen defaults the employment contract
        // to complete, so a worker added a minute ago is recorded as having
        // signed one — on a register whose purpose is proving the paperwork
        // exists, that is the one default that must not be there.
        dbContext.EorComplianceItems.AddRange(
            Compliance.Starting(engagement.Id, employee.TenantId, now));

        // The employee's own record follows: where somebody is legally employed
        // is a fact about them, not only about the engagement, and the employee
        // screens read this column.
        employee.CountryOfEmployment = engagement.CountryOfEngagement;
        employee.UpdatedAt = now;

        audit.Record(
            AuditAction.Create,
            Module,
            $"{employee.FullName} engaged through {provider.Name} in "
            + $"{engagement.CountryOfEngagement}",
            entityId: engagement.Id,
            entityType: nameof(EorEngagement));

        await dbContext.SaveChangesAsync(cancellationToken);

        return EorResult<EorWorkerDto>.Ok(
            (await DescribeAsync([engagement], today, cancellationToken))[0]);
    }

    public async Task<EorResult<EorWorkerDto>> UpdateEngagementAsync(
        Guid id,
        EorEngagementRequest request,
        CancellationToken cancellationToken = default)
    {
        EorEngagement? engagement = await dbContext.EorEngagements
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (engagement is null)
        {
            return EorResult<EorWorkerDto>.Failed(EorOutcome.NotFound);
        }

        EorProvider? provider = await dbContext.EorProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == request.ProviderId, cancellationToken);

        if (provider is null)
        {
            return EorResult<EorWorkerDto>.Refused(EorRefusal.ProviderNotFound);
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        // Somebody else holding a live engagement, not this one. Editing a
        // worker must not refuse on the strength of their own record.
        bool alreadyEngaged = await dbContext.EorEngagements
            .AsNoTracking()
            .Where(row => row.EmployeeId == request.EmployeeId && row.Id != id)
            .AnyAsync(row => row.EndDate == null, cancellationToken);

        EorEngagement proposed = new()
        {
            Id = engagement.Id,
            TenantId = engagement.TenantId,
            EmployeeId = request.EmployeeId,
            ProviderId = request.ProviderId,
            CountryOfEngagement = (request.CountryOfEngagement ?? string.Empty).Trim(),
            Currency = (request.Currency ?? string.Empty).Trim().ToUpperInvariant(),
            GrossMonthly = request.GrossMonthly,
            EmployerCostMonthly = request.EmployerCostMonthly,
            PayFrequency = request.PayFrequency,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            UsdRate = request.UsdRate,
            RateOn = request.RateOn,
        };

        EorRefusal refusal = Engagements.Check(proposed, provider, alreadyEngaged);

        if (refusal != EorRefusal.None)
        {
            return EorResult<EorWorkerDto>.Refused(refusal);
        }

        engagement.ProviderId = proposed.ProviderId;
        engagement.CountryOfEngagement = proposed.CountryOfEngagement;
        engagement.Currency = proposed.Currency;
        engagement.GrossMonthly = proposed.GrossMonthly;
        engagement.EmployerCostMonthly = proposed.EmployerCostMonthly;
        engagement.PayFrequency = proposed.PayFrequency;
        engagement.StartDate = proposed.StartDate;
        engagement.EndDate = proposed.EndDate;
        engagement.UsdRate = proposed.UsdRate;
        engagement.RateOn = proposed.RateOn;
        engagement.UpdatedAt = timeProvider.GetUtcNow();

        audit.Record(
            AuditAction.Update,
            Module,
            $"Updated EOR engagement in {engagement.CountryOfEngagement}",
            entityId: engagement.Id,
            entityType: nameof(EorEngagement));

        await dbContext.SaveChangesAsync(cancellationToken);

        return EorResult<EorWorkerDto>.Ok(
            (await DescribeAsync([engagement], today, cancellationToken))[0]);
    }

    public async Task<EorResult<EorWorkerDto>> EndAsync(
        Guid id,
        EndEngagementRequest request,
        CancellationToken cancellationToken = default)
    {
        EorEngagement? engagement = await dbContext.EorEngagements
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (engagement is null)
        {
            return EorResult<EorWorkerDto>.Failed(EorOutcome.NotFound);
        }

        if (request.EndDate < engagement.StartDate)
        {
            return EorResult<EorWorkerDto>.Refused(EorRefusal.EndsBeforeStart);
        }

        engagement.EndDate = request.EndDate;
        engagement.UpdatedAt = timeProvider.GetUtcNow();

        audit.Record(
            AuditAction.Update,
            Module,
            string.IsNullOrWhiteSpace(request.Reason)
                ? $"EOR engagement ended on {request.EndDate:d MMMM yyyy}"
                : $"EOR engagement ended on {request.EndDate:d MMMM yyyy}: {request.Reason.Trim()}",
            entityId: engagement.Id,
            entityType: nameof(EorEngagement));

        await dbContext.SaveChangesAsync(cancellationToken);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        return EorResult<EorWorkerDto>.Ok(
            (await DescribeAsync([engagement], today, cancellationToken))[0]);
    }

    public async Task<EorOutcome> RemoveAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        EorEngagement? engagement = await dbContext.EorEngagements
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (engagement is null)
        {
            return EorOutcome.NotFound;
        }

        // The engagement goes; the employee stays. Removing somebody from the
        // EOR register means they are no longer engaged through a provider, not
        // that they never worked here — and deleting the person would take their
        // documents, leave and assets with them.
        dbContext.EorEngagements.Remove(engagement);

        audit.Record(
            AuditAction.Delete,
            Module,
            $"Removed EOR engagement in {engagement.CountryOfEngagement}",
            entityId: engagement.Id,
            entityType: nameof(EorEngagement));

        await dbContext.SaveChangesAsync(cancellationToken);

        return EorOutcome.Succeeded;
    }

    public async Task<EorSummary> SummaryAsync(CancellationToken cancellationToken = default)
    {
        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        List<EorEngagement> engagements = await dbContext.EorEngagements
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        List<EorProvider> providers = await dbContext.EorProviders
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        Dictionary<Guid, IReadOnlyList<EorComplianceItem>> compliance =
            await ComplianceByEngagementAsync(
                [.. engagements.Select(engagement => engagement.Id)], cancellationToken);

        return EorRollups.Summarise(engagements, providers, compliance, today);
    }

    // ── shared ─────────────────────────────────────────────────────────────

    internal async Task<Dictionary<Guid, IReadOnlyList<EorComplianceItem>>>
        ComplianceByEngagementAsync(
            IReadOnlyList<Guid> engagementIds,
            CancellationToken cancellationToken)
    {
        List<EorComplianceItem> items = await dbContext.EorComplianceItems
            .AsNoTracking()
            .Where(item => engagementIds.Contains(item.EngagementId))
            .ToListAsync(cancellationToken);

        return items
            .GroupBy(item => item.EngagementId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<EorComplianceItem>)[.. group]);
    }

    internal async Task<List<EorWorkerDto>> DescribeAsync(
        IReadOnlyList<EorEngagement> engagements,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        if (engagements.Count == 0)
        {
            return [];
        }

        List<Guid> providerIds = [.. engagements.Select(engagement => engagement.ProviderId).Distinct()];
        List<Guid> employeeIds = [.. engagements.Select(engagement => engagement.EmployeeId).Distinct()];

        Dictionary<Guid, EorProvider> providers = await dbContext.EorProviders
            .AsNoTracking()
            .Where(provider => providerIds.Contains(provider.Id))
            .ToDictionaryAsync(provider => provider.Id, cancellationToken);

        // The person's details read from employees, never copied onto the
        // engagement. The screen stores providerName and currencySymbol on the
        // worker, which go stale the moment a provider is renamed.
        var people = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employeeIds.Contains(employee.Id))
            .Select(employee => new
            {
                employee.Id,
                employee.FirstName,
                employee.MiddleName,
                employee.LastName,
                employee.PreferredName,
                employee.Initials,
                employee.Email,
                employee.JobTitle,
                Department = employee.DepartmentId == null
                    ? null
                    : dbContext.Departments
                        .Where(department => department.Id == employee.DepartmentId)
                        .Select(department => department.Name)
                        .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        Dictionary<Guid, IReadOnlyList<EorComplianceItem>> compliance =
            await ComplianceByEngagementAsync(
                [.. engagements.Select(engagement => engagement.Id)], cancellationToken);

        List<EorWorkerDto> described = [];

        foreach (EorEngagement engagement in engagements)
        {
            if (!providers.TryGetValue(engagement.ProviderId, out EorProvider? provider))
            {
                continue;
            }

            var person = people.FirstOrDefault(row => row.Id == engagement.EmployeeId);

            IReadOnlyList<EorComplianceItem> items =
                compliance.TryGetValue(engagement.Id, out IReadOnlyList<EorComplianceItem>? held)
                    ? held
                    : [];

            string name = person is null
                ? "Unknown"
                : string.IsNullOrWhiteSpace(person.PreferredName)
                    ? $"{person.FirstName} {person.LastName}".Trim()
                    : $"{person.PreferredName} {person.LastName}".Trim();

            described.Add(new EorWorkerDto
            {
                Id = engagement.Id,
                EmployeeId = engagement.EmployeeId,
                Name = name,
                Initials = person?.Initials,
                Email = person?.Email,
                JobTitle = person?.JobTitle,
                Department = person?.Department,
                ProviderId = provider.Id,
                ProviderName = provider.Name,
                CountryOfEngagement = engagement.CountryOfEngagement,
                Stage = Engagements.StageOf(engagement, today),
                StartDate = engagement.StartDate,
                EndDate = engagement.EndDate,
                PayFrequency = engagement.PayFrequency,
                Currency = engagement.Currency,
                GrossMonthly = engagement.GrossMonthly,
                EmployerCostMonthly = engagement.EmployerCostMonthly,
                ManagementFee = Engagements.ManagementFee(engagement, provider),
                TotalMonthly = Engagements.TotalMonthly(engagement, provider),
                UsdRate = engagement.UsdRate,
                RateOn = engagement.RateOn,
                TotalUsd = Engagements.TotalUsd(engagement, provider),
                RateIsStale = Engagements.RateIsStale(engagement, today),
                Compliance = DescribeCompliance(items, today),
                Blockers = Compliance.Blockers(items, today),
                FullyCompliant = Compliance.IsFullyCompliant(items, today),
            });
        }

        return described;
    }

    internal static IReadOnlyList<ComplianceItemDto> DescribeCompliance(
        IReadOnlyList<EorComplianceItem> items,
        DateOnly today) =>
        [
            .. ComplianceCatalogue.All.Select(requirement =>
            {
                EorComplianceItem? item =
                    items.FirstOrDefault(held => held.Key == requirement.Key);

                ComplianceState state = item?.State ?? ComplianceState.Pending;

                ComplianceStanding standing = item is null
                    ? ComplianceStanding.Pending
                    : Compliance.StandingOf(item, today);

                return new ComplianceItemDto
                {
                    Key = requirement.Key,
                    Label = requirement.Label,
                    State = state,
                    Standing = standing,
                    Note = item?.Note,
                    ExpiresOn = item?.ExpiresOn,
                    VerifiedOn = item?.VerifiedOn,
                    EvidenceFileId = item?.EvidenceFileId,
                    Expires = requirement.Expires,
                    BlocksWork = requirement.BlocksWork,

                    // Reported rather than refused: a provider confirming
                    // something by email is still a record, and the register
                    // should show what rests on somebody's word.
                    Unevidenced = state == ComplianceState.Complete
                        && item?.EvidenceFileId is null,
                };
            }),
        ];

    private static EorProviderDto Describe(
        EorProvider provider,
        IReadOnlyList<EorEngagement> engagements,
        DateOnly today) => new()
        {
            Id = provider.Id,
            Name = provider.Name,
            Status = provider.Status,
            CountriesCovered = provider.CountriesCovered,
            ManagementFeePercent = provider.ManagementFeePercent,
            PartneredOn = provider.PartneredOn,
            Website = provider.Website,
            WorkerCount = EorRollups.WorkersFor(engagements, provider.Id, today),
        };

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
