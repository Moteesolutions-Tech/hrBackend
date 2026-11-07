using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Diversity;
using Motee.Application.Tenancy;
using Motee.Domain.Common;
using Motee.Domain.Diversity;
using Motee.Domain.Employees;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Diversity;

// Self-declared diversity data.
//
// The shape of this file is the policy. There is no method here that takes an
// employee id and writes, and no method that takes an employee id and returns
// an answer — the only route to an individual declaration resolves the caller
// and hands back their own. Reporting goes through a tally that folds away
// any group small enough to identify somebody.
internal sealed class DiversityService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentEmployee currentEmployee,
    TimeProvider timeProvider) : IDiversityService
{
    private static readonly EmployeeStatus[] Eligible =
    [
        EmployeeStatus.Probation,
        EmployeeStatus.Active,
        EmployeeStatus.OnLeave,
        EmployeeStatus.Offboarding,
    ];

    public async Task<DiversityFormDto> MineAsync(CancellationToken cancellationToken = default)
    {
        CountryCode country = await CountryAsync(cancellationToken);
        Guid? employeeId = await currentEmployee.IdAsync(cancellationToken);

        DiversityDeclaration? mine = employeeId is Guid id
            ? await dbContext.DiversityDeclarations
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.EmployeeId == id, cancellationToken)
            : null;

        return Describe(country, mine);
    }

    public async Task<DiversityResult> DeclareAsync(
        DiversityRequest request,
        CancellationToken cancellationToken = default)
    {
        CountryCode country = await CountryAsync(cancellationToken);

        // No employee record, nothing to declare. The account owner is
        // permanently here: they run the company rather than work there.
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return DiversityResult.Failed(DiversityOutcome.NoEmployeeRecord);
        }

        // Every answer checked against what this country actually offers, so
        // a client sending an ethnicity to a Nigerian tenant is refused
        // rather than quietly stored. A form that does not ask a question is
        // not the same as an API that will not take the answer.
        foreach ((DiversityField field, string? answer) in (
            (DiversityField, string?)[])
        [
            (DiversityField.Ethnicity, request.Ethnicity),
            (DiversityField.Disability, request.Disability),
            (DiversityField.Religion, request.Religion),
            (DiversityField.VeteranStatus, request.VeteranStatus),
            (DiversityField.CaringResponsibilities, request.CaringResponsibilities),
        ])
        {
            if (answer is not null && !Diversities.IsOffered(country, field, answer))
            {
                return DiversityResult.Refused(field);
            }
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        DiversityDeclaration? mine = await dbContext.DiversityDeclarations
            .FirstOrDefaultAsync(row => row.EmployeeId == employeeId, cancellationToken);

        if (mine is null)
        {
            mine = new DiversityDeclaration
            {
                Id = Guid.NewGuid(),
                TenantId = currentTenant.TenantId ?? Guid.Empty,
                EmployeeId = employeeId,
                DeclaredAt = now,
            };

            dbContext.DiversityDeclarations.Add(mine);
        }

        // Absent leaves what they said before. Clearing an answer is done by
        // choosing "prefer not to say", which is an answer.
        mine.Ethnicity = request.Ethnicity ?? mine.Ethnicity;
        mine.Disability = request.Disability ?? mine.Disability;
        mine.Religion = request.Religion ?? mine.Religion;
        mine.VeteranStatus = request.VeteranStatus ?? mine.VeteranStatus;
        mine.CaringResponsibilities =
            request.CaringResponsibilities ?? mine.CaringResponsibilities;

        mine.Jurisdiction = country;
        mine.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return DiversityResult.Ok(Describe(country, mine));
    }

    public async Task<DiversityReportDto> ReportAsync(
        DiversityReportQuery query,
        CancellationToken cancellationToken = default)
    {
        CountryCode country = await CountryAsync(cancellationToken);

        IQueryable<Employee> people = dbContext.Employees
            .AsNoTracking()
            .Where(employee => Eligible.Contains(employee.Status));

        if (query.DepartmentId is Guid departmentId)
        {
            people = people.Where(employee => employee.DepartmentId == departmentId);
        }

        if (query.BranchId is Guid branchId)
        {
            people = people.Where(employee => employee.BranchId == branchId);
        }

        if (query.EmployeeIds.Count > 0)
        {
            List<Guid> wanted = [.. query.EmployeeIds];

            people = people.Where(employee => wanted.Contains(employee.Id));
        }

        // Ids only, and they do not leave this method. The declarations are
        // pulled by id and immediately reduced to counts.
        List<Guid> ids = await people
            .Select(employee => employee.Id)
            .ToListAsync(cancellationToken);

        List<DiversityDeclaration> declarations = ids.Count == 0
            ? []
            : await dbContext.DiversityDeclarations
                .AsNoTracking()
                .Where(row => ids.Contains(row.EmployeeId))
                .ToListAsync(cancellationToken);

        List<DiversityBreakdownDto> breakdowns = [];

        foreach (DiversityCategory category in Diversities.CategoriesFor(country))
        {
            SuppressedBreakdown tally = Suppression.Tally(
                [.. declarations.Select(row => Diversities.ValueOf(row, category.Field))],
                ids.Count);

            breakdowns.Add(new DiversityBreakdownDto
            {
                Field = category.Field,
                Label = category.Label,
                Rows = tally.Rows,
                SuppressedCount = tally.SuppressedCount,
                SuppressedGroups = tally.SuppressedGroups,
                Declared = tally.Declared,
                FullySuppressed = tally.FullySuppressed,
            });
        }

        // Answered anything at all, which is what the declaration rate is
        // about: a breakdown built from a fifth of the staff is a different
        // claim from one built from all of them.
        int declared = declarations.Count(row => Diversities
            .CategoriesFor(country)
            .Any(category => Diversities.ValueOf(row, category.Field) is not null));

        return new DiversityReportDto
        {
            Eligible = ids.Count,
            Declared = declared,
            DeclarationRatePercent = Suppression.DeclarationRate(declared, ids.Count),
            Breakdowns = breakdowns,
        };
    }

    private static DiversityFormDto Describe(
        CountryCode country,
        DiversityDeclaration? mine) => new()
        {
            Categories =
            [
                .. Diversities.CategoriesFor(country).Select(category => new DiversityCategoryDto
                {
                    Field = category.Field,
                    Label = category.Label,
                    Purpose = category.Purpose,
                    Options = category.Options,
                    Answer = mine is null ? null : Diversities.ValueOf(mine, category.Field),
                }),
            ],
            DeclaredAt = mine?.DeclaredAt,
        };

    // Which question set applies. Scoped by id, because tenants are not
    // themselves tenant-scoped — an unfiltered read returns some arbitrary
    // company's row and would ask a Nigerian employee the UK questions.
    private async Task<CountryCode> CountryAsync(CancellationToken cancellationToken)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return CountryCode.Nigeria;
        }

        Tenant? tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == tenantId, cancellationToken);

        return tenant?.CountryCode ?? CountryCode.Nigeria;
    }
}
