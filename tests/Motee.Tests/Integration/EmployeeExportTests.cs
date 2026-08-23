using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Employees;
using Motee.Application.Exports;
using Motee.Application.Organisation;
using Motee.Domain.Authorization;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Exports;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class EmployeeExportTests(PostgresFixture fixture)
{
    private async Task<(Guid TenantId, ServiceProvider Provider)> ArrangeAsync(int employees = 3)
    {
        await fixture.ResetAsync();

        Guid tenantId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Acme",
                Slug = $"acme-{tenantId:N}",
                CountryCode = CountryCode.Nigeria,
            });

            await seed.SaveChangesAsync();
        }

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = tenantId;

        Guid departmentId = (await provider.CreateScope().ServiceProvider
            .GetRequiredService<IDepartmentService>()
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        for (int index = 1; index <= employees; index++)
        {
            await provider.CreateScope().ServiceProvider
                .GetRequiredService<IEmployeeService>()
                .CreateAsync(new EmployeeRequest
                {
                    FirstName = $"Person{index:D2}",
                    LastName = "Test",
                    Email = $"person{index:D2}@acme.com",
                    Phone = "08012345678",
                    JobTitle = "Engineer",
                    DepartmentId = departmentId,
                    EmploymentType = EmploymentType.FullTime,
                });
        }

        return (tenantId, provider);
    }

    private static IExportService Exports(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IExportService>();

    private static EmployeeExportFilters AllScope() => new() { Scope = DataScope.Everything };

    // The service hands out signed links rather than bytes, so the file itself is
    // read straight from storage.
    private static async Task<string> ReadCsvAsync(ServiceProvider provider, Guid exportId)
    {
        Guid tenantId = provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId!.Value;
        PostgresFixture.InMemoryFileStorage storage =
            provider.GetRequiredService<PostgresFixture.InMemoryFileStorage>();

        string key = storage.Keys.Single(candidate =>
            candidate.StartsWith($"exports/{tenantId:N}/{exportId:N}/", StringComparison.Ordinal));

        await using Stream content = (await storage.OpenReadAsync(key))!;
        using StreamReader reader = new(content, Encoding.UTF8);

        return await reader.ReadToEndAsync();
    }

    // The queue runs inline in tests, so requesting is enough to finish it.
    [SkippableFact]
    public async Task AnExportRunsAndBecomesDownloadable()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        ExportJobDto queued = await Exports(owned).RequestEmployeeExportAsync(AllScope());
        ExportJobDto finished = (await Exports(owned).GetAsync(queued.Id))!;

        Assert.Equal(ExportStatus.Completed, finished.Status);
        Assert.Equal(3, finished.RowCount);
        Assert.EndsWith(".csv", finished.FileName!, StringComparison.Ordinal);
        Assert.NotNull(finished.ExpiresAt);
    }

    [SkippableFact]
    public async Task TheFileHasAHeaderAndOneLinePerEmployee()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        ExportJobDto job = await Exports(owned).RequestEmployeeExportAsync(AllScope());
        string csv = await ReadCsvAsync(owned, job.Id);

        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, lines.Length);
        Assert.Contains("person01@acme.com", csv, StringComparison.Ordinal);
    }

    // More rows than a single page, so the paging loop runs more than once.
    [SkippableFact]
    public async Task EveryRowIsIncludedAcrossPages()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync(employees: 12);
        await using ServiceProvider owned = provider;

        ExportJobDto job = await Exports(owned).RequestEmployeeExportAsync(AllScope());

        Assert.Equal(12, (await Exports(owned).GetAsync(job.Id))!.RowCount);
    }

    [SkippableFact]
    public async Task FiltersAreHonouredByTheJob()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        ExportJobDto job = await Exports(owned)
            .RequestEmployeeExportAsync(AllScope() with { Search = "person02" });

        Assert.Equal(1, (await Exports(owned).GetAsync(job.Id))!.RowCount);
    }

    // The range is serialised into the job row and rebuilt on the worker, so a
    // DateOnly that survives the round trip is the thing worth pinning.
    [SkippableFact]
    public async Task AStartDateRangeSurvivesIntoTheJob()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync(employees: 0);
        await using ServiceProvider owned = provider;

        Guid departmentId = (await owned.CreateScope().ServiceProvider
            .GetRequiredService<IDepartmentService>()
            .ListAsync()).Single().Id;

        await Hire(owned, departmentId, "early@acme.com", new DateOnly(2024, 3, 1));
        await Hire(owned, departmentId, "late@acme.com", new DateOnly(2025, 9, 1));

        ExportJobDto job = await Exports(owned).RequestEmployeeExportAsync(AllScope() with
        {
            StartedFrom = new DateOnly(2025, 1, 1),
        });

        Assert.Equal(1, (await Exports(owned).GetAsync(job.Id))!.RowCount);
        Assert.Contains("late@acme.com", await ReadCsvAsync(owned, job.Id), StringComparison.Ordinal);
    }

    private static async Task Hire(
        ServiceProvider provider,
        Guid departmentId,
        string email,
        DateOnly startDate) =>
        await provider.CreateScope().ServiceProvider
            .GetRequiredService<IEmployeeService>()
            .CreateAsync(new EmployeeRequest
            {
                FirstName = email.Split('@')[0],
                LastName = "Test",
                Email = email,
                Phone = "08012345678",
                JobTitle = "Engineer",
                DepartmentId = departmentId,
                EmploymentType = EmploymentType.FullTime,
                StartDate = startDate,
            });

    [SkippableFact]
    public async Task EveryColumnIsWrittenByDefault()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync(employees: 1);
        await using ServiceProvider owned = provider;

        ExportJobDto job = await Exports(owned).RequestEmployeeExportAsync(AllScope());
        string header = (await ReadCsvAsync(owned, job.Id)).Split("\r\n")[0];

        Assert.Equal(
            EmployeeExportColumns.All.Count,
            header.Split(',').Length);
    }

    // The caller chooses the columns; the server does not decide the shape of a file
    // someone is going to open in a spreadsheet.
    [SkippableFact]
    public async Task OnlyTheRequestedColumnsAreWritten()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync(employees: 1);
        await using ServiceProvider owned = provider;

        ExportJobDto job = await Exports(owned)
            .RequestEmployeeExportAsync(AllScope() with { Columns = ["name", "email"] });

        string[] lines = (await ReadCsvAsync(owned, job.Id))
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("\"Name\",\"Email\"", lines[0]);
        Assert.Equal(2, lines[1].Split(',').Length);
    }

    // A saved view naming a column that has since gone should still export.
    [SkippableFact]
    public async Task UnknownColumnsFallBackRatherThanFailing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync(employees: 1);
        await using ServiceProvider owned = provider;

        ExportJobDto job = await Exports(owned)
            .RequestEmployeeExportAsync(AllScope() with { Columns = ["name", "notAColumn"] });

        string header = (await ReadCsvAsync(owned, job.Id)).Split("\r\n")[0];

        Assert.Equal("\"Name\"", header);
    }

    // Delivery is a signed link to storage, not bytes through the API.
    [SkippableFact]
    public async Task AFinishedExportYieldsASignedLink()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        ExportJobDto job = await Exports(owned).RequestEmployeeExportAsync(AllScope());
        ExportLink? link = await Exports(owned).CreateLinkAsync(job.Id);

        Assert.NotNull(link);
        Assert.Contains(job.FileName!, link.Url, StringComparison.Ordinal);
        Assert.EndsWith(".csv", link.FileName, StringComparison.Ordinal);
    }

    // The link is unauthenticated once issued, so it must not last as long as the
    // export itself — asking for another one needs a session.
    [SkippableFact]
    public async Task TheLinkExpiresBeforeTheExportDoes()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        ExportJobDto job = await Exports(owned).RequestEmployeeExportAsync(AllScope());
        ExportLink link = (await Exports(owned).CreateLinkAsync(job.Id))!;

        Assert.True(link.LinkExpiresAt < job.ExpiresAt);
        Assert.True(ExportPolicy.LinkLifetime < ExportPolicy.Retention);
    }

    // A stale link should not force the whole export to be re-run.
    [SkippableFact]
    public async Task AFreshLinkCanBeMintedWhileTheExportLives()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        ExportJobDto job = await Exports(owned).RequestEmployeeExportAsync(AllScope());

        Assert.NotNull(await Exports(owned).CreateLinkAsync(job.Id));
        Assert.NotNull(await Exports(owned).CreateLinkAsync(job.Id));
    }

    // The rows in a file were chosen by one person's scope. A colleague on the same
    // tenant with a narrower one must not reach it by knowing the id.
    [SkippableFact]
    public async Task AnotherUserInTheSameTenantCannotReachTheExport()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        ExportJobDto job = await Exports(owned).RequestEmployeeExportAsync(AllScope());

        // Same tenant, same session — only the person changes.
        owned.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            Guid.NewGuid().ToString();

        Assert.Null(await Exports(owned).GetAsync(job.Id));
        Assert.Null(await Exports(owned).CreateLinkAsync(job.Id));
    }

    // The whole point of capturing scope at request time.
    [SkippableFact]
    public async Task ScopeIsReplayedRatherThanWidened()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        ExportJobDto job = await Exports(owned)
            .RequestEmployeeExportAsync(new EmployeeExportFilters { Scope = DataScope.Nothing });

        Assert.Equal(0, (await Exports(owned).GetAsync(job.Id))!.RowCount);
    }

    [SkippableFact]
    public async Task AnExportIsNotVisibleToAnotherTenant()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        ExportJobDto job = await Exports(owned).RequestEmployeeExportAsync(AllScope());

        Guid otherTenantId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = otherTenantId,
                Name = "Globex",
                Slug = $"globex-{otherTenantId:N}",
                CountryCode = CountryCode.UnitedKingdom,
            });

            await seed.SaveChangesAsync();
        }

        owned.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = otherTenantId;

        Assert.Null(await Exports(owned).GetAsync(job.Id));
        Assert.Null(await Exports(owned).CreateLinkAsync(job.Id));
    }

    // Personal data does not stay downloadable for ever.
    [SkippableFact]
    public async Task AnExpiredExportIsNoLongerDownloadable()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        ExportJobDto job = await Exports(owned).RequestEmployeeExportAsync(AllScope());

        await using (MoteeDbContext context = fixture.CreateContext(tenantId))
        {
            ExportJob row = (await context.ExportJobs.FindAsync(job.Id))!;
            row.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await context.SaveChangesAsync();
        }

        Assert.Null(await Exports(owned).CreateLinkAsync(job.Id));
    }

    [SkippableFact]
    public async Task AnUnknownExportIsNotDownloadable()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Assert.Null(await Exports(owned).CreateLinkAsync(Guid.NewGuid()));
    }

    // The job runs without a request, so nothing resolves a tenant from a token. It
    // reads the tenant from its own row — without that it would export nothing.
    [SkippableFact]
    public async Task TheJobFindsItsTenantWithoutARequest()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        ExportJobDto job = await Exports(owned).RequestEmployeeExportAsync(AllScope());

        await using MoteeDbContext context = fixture.CreateContext(tenantId);
        ExportJob row = (await context.ExportJobs.FindAsync(job.Id))!;

        Assert.Equal(tenantId, row.TenantId);
        Assert.Equal(3, row.RowCount);
        Assert.StartsWith($"exports/{tenantId:N}/", row.FileKey!, StringComparison.Ordinal);
    }
}
