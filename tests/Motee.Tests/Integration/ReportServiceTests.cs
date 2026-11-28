using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Leave;
using Motee.Application.Organisation;
using Motee.Application.Reports;
using Motee.Domain.Attendance;
using Motee.Domain.Authorization;
using Motee.Domain.Contracts;
using Motee.Domain.Employees;
using Motee.Domain.Learning;
using Motee.Domain.Organisation;
using Motee.Domain.Performance;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Reporting over the declared datasets.
//
// Two things are being pinned. That a report carries the same narrowing as the screen
// it reports on — a report is the same data in a different shape, and the scope has to
// come from the dataset's own module rather than from the reports endpoint. And that a
// filter the dataset cannot honour is refused rather than ignored, because a report
// that quietly drops "department = Engineering" answers a different question and looks
// right doing it.
[Collection(PostgresCollection.Name)]
public class ReportServiceTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _ownerUserId;
    private Guid _engineeringId;
    private Guid _salesId;

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Owner",
            LastName = "Okafor",
            Email = "owner@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;
        _ownerUserId = registration.UserId;

        ServiceProvider provider = fixture.BuildProvider();

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            _ownerUserId.ToString();

        _engineeringId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _salesId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Sales", Code = "SAL" }))
            .Department!.Id;

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IReportService Reports(ServiceProvider provider) =>
        Resolve<IReportService>(provider);

    private async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        Guid departmentId,
        Guid? managerId = null,
        DateOnly? dateOfBirth = null,
        string? gender = null,
        string? grade = null,
        Guid? branchId = null,
        string? workLocation = null,
        DateOnly? startDate = null) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Adeyemi",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            ManagerId = managerId,
            DateOfBirth = dateOfBirth,
            Gender = gender,
            Grade = grade,
            BranchId = branchId,
            WorkLocation = workLocation,
            StartDate = startDate ?? new DateOnly(2021, 1, 10),
        })).Employee!.Id;

    private static Task<ReportRunResult> RunAsync(
        ServiceProvider provider,
        string dataset,
        IReadOnlyDictionary<string, string?>? filters = null,
        IReadOnlyList<string>? columns = null) =>
        Reports(provider).RunAsync(new ReportRunRequest
        {
            Dataset = dataset,
            Filters = filters,
            Columns = columns,
            PageSize = 200,
        });

    // ── the catalogue ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheOwnerIsOfferedEveryDatasetWithItsRealCount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);
        await HireAsync(provider, "Ben", _salesId);

        IReadOnlyList<ReportDatasetDto> offered = await Reports(provider).DatasetsAsync();

        Assert.Equal(ReportCatalogue.All.Count, offered.Count);

        ReportDatasetDto employees = offered.First(dataset => dataset.Key == "employees");

        // The count comes through the same provider the report runs through, so it is
        // the number of rows the caller would actually get — not the size of the table.
        Assert.Equal(2, employees.RecordCount);
        Assert.Equal("Employees / Headcount", employees.Label);
        Assert.NotEmpty(employees.Columns);
    }

    // The case that makes the Self rule necessary. SelfServicePolicy grants every
    // signed-in employee View at Self scope on these modules, so "any scope at all"
    // would list every dataset to everybody — and a report of yourself is what your
    // own screens are for.
    [SkippableFact]
    public async Task AnOrdinaryEmployeeIsOfferedNoDatasets()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid adaId = await HireAsync(provider, "Ada", _engineeringId);

        await ActAsync(provider, adaId, "ada");

        Assert.Empty(await Reports(provider).DatasetsAsync());

        // And running one anyway comes back as though it did not exist, rather than
        // confirming that something they cannot read is there.
        Assert.Equal(
            ReportOutcome.UnknownDataset,
            (await RunAsync(provider, "employees")).Outcome);
    }

    // ── rows ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AReportReturnsTheColumnsAskedForInOrder()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);

        ReportRunResult run = await RunAsync(
            provider, "employees", columns: ["department", "fullName"]);

        Assert.True(run.Succeeded, run.Outcome.ToString());

        ReportTableDto table = run.Table!;

        Assert.Equal(["department", "fullName"], table.Columns.Select(c => c.Key).ToArray());

        IReadOnlyDictionary<string, string?> row = Assert.Single(table.Rows.Items);

        // Only what was asked for. Building every column and letting the client pick
        // would put a phone number in a payload somebody deliberately left out.
        Assert.Equal(["department", "fullName"], row.Keys.Order().ToArray().Order().ToArray());
        Assert.Equal("Engineering", row["department"]);
        Assert.Equal("Ada Adeyemi", row["fullName"]);
    }

    [SkippableFact]
    public async Task TheDayTheFiguresWereReadIsOnTheAnswer()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ReportRunResult run = await RunAsync(provider, "employees");

        Assert.True(run.Succeeded, run.Outcome.ToString());
        Assert.Equal(
            DateOnly.FromDateTime(DateTime.UtcNow), run.Table!.AsOf);
    }

    // ── filters ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AFilterNarrowsByName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);
        await HireAsync(provider, "Ben", _salesId);

        ReportRunResult run = await RunAsync(
            provider,
            "employees",
            new Dictionary<string, string?> { ["department"] = "Engineering" });

        Assert.True(run.Succeeded, run.Outcome.ToString());

        IReadOnlyDictionary<string, string?> row = Assert.Single(run.Table!.Rows.Items);

        Assert.Equal("Ada Adeyemi", row["fullName"]);
    }

    // Case-insensitive, because a filter carries what somebody typed.
    [SkippableFact]
    public async Task ADepartmentFilterIgnoresCase()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);

        ReportRunResult run = await RunAsync(
            provider,
            "employees",
            new Dictionary<string, string?> { ["department"] = "engineering" });

        Assert.True(run.Succeeded, run.Outcome.ToString());
        Assert.Single(run.Table!.Rows.Items);
    }

    // No rows rather than every row. A filter naming a department that does not exist
    // has an answer, and it is not "here is the whole company".
    [SkippableFact]
    public async Task AFilterForSomethingThatDoesNotExistReturnsNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);

        ReportRunResult run = await RunAsync(
            provider,
            "employees",
            new Dictionary<string, string?> { ["department"] = "Marketing" });

        Assert.True(run.Succeeded, run.Outcome.ToString());
        Assert.Empty(run.Table!.Rows.Items);
        Assert.Equal(0, run.Table.Rows.TotalItems);
    }

    [SkippableFact]
    public async Task AStatusThatIsNotAStatusReturnsNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);

        ReportRunResult run = await RunAsync(
            provider,
            "employees",
            new Dictionary<string, string?> { ["status"] = "Purple" });

        // Not the whole table, which would be the worst of the available answers.
        Assert.True(run.Succeeded, run.Outcome.ToString());
        Assert.Empty(run.Table!.Rows.Items);
    }

    // The refusal that matters. Ignoring it would answer a different question.
    [SkippableFact]
    public async Task AFilterOnAColumnThatCannotBeNarrowedIsRefusedByName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ReportRunResult refused = await RunAsync(
            provider,
            "employees",
            new Dictionary<string, string?> { ["email"] = "ada@" });

        Assert.Equal(ReportOutcome.NotFilterable, refused.Outcome);
        Assert.Equal("email", refused.Column);
    }

    [SkippableFact]
    public async Task AnUnknownDatasetIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            ReportOutcome.UnknownDataset,
            (await RunAsync(provider, "payroll")).Outcome);
    }

    // ── the leave dataset ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheLeaveReportReadsTheLeaveRegister()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid adaId = await HireAsync(provider, "Ada", _engineeringId);

        Guid annualTypeId;

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            annualTypeId = await seed.LeaveTypes
                .Where(type => type.Code == "annual")
                .Select(type => type.Id)
                .FirstAsync();
        }

        DateOnly start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);

        Assert.True((await Resolve<ILeaveRequestService>(provider).SubmitAsync(
            new LeaveRequestSubmission
            {
                EmployeeId = adaId,
                LeaveTypeId = annualTypeId,
                StartDate = start,
                EndDate = start.AddDays(2),
                Reason = "Holiday",
            })).Succeeded);

        ReportRunResult run = await RunAsync(provider, "leave");

        Assert.True(run.Succeeded, run.Outcome.ToString());

        IReadOnlyDictionary<string, string?> row = Assert.Single(run.Table!.Rows.Items);

        Assert.Equal("Ada Adeyemi", row["employee"]);
        Assert.Equal("Annual Leave", row["type"]);
        Assert.Equal("Engineering", row["department"]);

        // ISO on the wire, so the value means one day whatever the reader's locale.
        Assert.Equal(start.ToString("yyyy-MM-dd"), row["startDate"]);
        Assert.Equal("false", row["isHalfDay"]);
    }

    // ── signing in as somebody ──────────────────────────────────────────────

    private async Task ActAsync(ServiceProvider provider, Guid employeeId, string handle)
    {
        Guid userId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.Users.Add(new ApplicationUser
            {
                Id = userId,
                TenantId = _tenantId,
                EmployeeId = employeeId,
                FirstName = handle,
                LastName = "Adeyemi",
                Email = $"{handle}@acme.com",
                NormalizedEmail = $"{handle.ToUpperInvariant()}@ACME.COM",
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();
    }

    // ── the columns the reports screen reads ────────────────────────────────

    // Derived against the tenant's own today rather than stored, so a birthday does not
    // need a nightly job to become true. Pinned with dates relative to today so the
    // test does not quietly start failing next year.
    [SkippableFact]
    public async Task AgeAndTenureAreDerivedRatherThanStored()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        await HireAsync(
            provider,
            "Ada",
            _engineeringId,
            dateOfBirth: today.AddYears(-30),
            startDate: today.AddYears(-2));

        ReportRunResult run = await RunAsync(
            provider, "employees", columns: ["age", "tenureYears"]);

        Assert.True(run.Succeeded, run.Outcome.ToString());

        IReadOnlyDictionary<string, string?> row = Assert.Single(run.Table!.Rows.Items);

        Assert.Equal("30", row["age"]);
        Assert.Equal("2", row["tenureYears"]);
    }

    // Empty rather than "0". The age breakdown buckets this column, and a missing date
    // of birth landing in the under-twenties is a chart that is wrong rather than
    // incomplete.
    [SkippableFact]
    public async Task AMissingDateOfBirthGivesNoAgeRatherThanZero()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);

        ReportRunResult run = await RunAsync(provider, "employees", columns: ["age"]);

        Assert.Equal(string.Empty, Assert.Single(run.Table!.Rows.Items)["age"]);
    }

    [SkippableFact]
    public async Task GenderAndGradeComeThrough()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(
            provider, "Ada", _engineeringId, gender: "Female", grade: "G7");

        ReportRunResult run = await RunAsync(
            provider, "employees", columns: ["gender", "grade"]);

        IReadOnlyDictionary<string, string?> row = Assert.Single(run.Table!.Rows.Items);

        Assert.Equal("Female", row["gender"]);
        Assert.Equal("G7", row["grade"]);
    }

    // Branch region, then branch city, then the employee's own work location — the
    // chain the reports screen uses, so a region chart drawn from this agrees with it.
    [SkippableFact]
    public async Task RegionPrefersTheBranchAndFallsBackToTheWorkLocation()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid lagos = (await Resolve<IBranchService>(provider).CreateAsync(new BranchRequest
        {
            Name = "Lagos Office",
            Code = "LAG",
            City = "Lagos",
            Region = "South West",
        })).Branch!.Id;

        // A branch with a city but no region, to prove the fallback rather than assume
        // it.
        Guid abuja = (await Resolve<IBranchService>(provider).CreateAsync(new BranchRequest
        {
            Name = "Abuja Office",
            Code = "ABJ",
            City = "Abuja",
        })).Branch!.Id;

        await HireAsync(provider, "Ada", _engineeringId, branchId: lagos);
        await HireAsync(provider, "Ben", _engineeringId, branchId: abuja);
        await HireAsync(provider, "Cara", _engineeringId, workLocation: "Remote — Kenya");
        await HireAsync(provider, "Dele", _engineeringId);

        ReportRunResult run = await RunAsync(
            provider, "employees", columns: ["fullName", "region"]);

        Dictionary<string, string?> byName = run.Table!.Rows.Items
            .ToDictionary(row => row["fullName"]!, row => row["region"]);

        Assert.Equal("South West", byName["Ada Adeyemi"]);
        Assert.Equal("Abuja", byName["Ben Adeyemi"]);
        Assert.Equal("Remote — Kenya", byName["Cara Adeyemi"]);

        // Honest bucket rather than a blank slice in a region chart.
        Assert.Equal("Unspecified", byName["Dele Adeyemi"]);
    }

    // ── the datasets added for the reports screen ───────────────────────────
    //
    // The catalogue tests pin the keys and the dataset probe proves each provider runs.
    // Neither catches a typo in a provider's switch, which returns null for that column
    // and draws an empty chart — so each dataset gets one row read back.

    [SkippableFact]
    public async Task TheAttendanceReportReadsTheDaysRegister()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid adaId = await HireAsync(provider, "Ada", _engineeringId);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.AttendanceRecords.Add(new AttendanceRecord
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                EmployeeId = adaId,
                Date = today,

                // Status and hours are both derived from the clock rather than stored.
                ClockInAt = new DateTimeOffset(today, new TimeOnly(9, 0), TimeSpan.Zero),
                ClockOutAt = new DateTimeOffset(today, new TimeOnly(17, 30), TimeSpan.Zero),
            });

            await seed.SaveChangesAsync();
        }

        ReportRunResult run = await RunAsync(provider, "attendance");

        Assert.True(run.Succeeded, run.Outcome.ToString());

        IReadOnlyDictionary<string, string?> row = Assert.Single(run.Table!.Rows.Items);

        Assert.Equal("Ada Adeyemi", row["employee"]);
        Assert.Equal("Engineering", row["department"]);
        Assert.Equal(today.ToString("yyyy-MM-dd"), row["date"]);

        // Wall clock, not an instant: a column headed "Clock In" wants 09:00.
        Assert.Equal("09:00", row["clockIn"]);
        Assert.Equal("17:30", row["clockOut"]);
        Assert.False(string.IsNullOrWhiteSpace(row["hours"]));
        Assert.False(string.IsNullOrWhiteSpace(row["status"]));
    }

    [SkippableFact]
    public async Task ThePerformanceReportReadsTheReviewRegister()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid adaId = await HireAsync(provider, "Ada", _engineeringId);

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.PerformanceReviews.Add(new PerformanceReview
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                EmployeeId = adaId,
                Period = "H1 2026",
                Status = ReviewStatus.Completed,
                DueDate = new DateOnly(2026, 6, 30),
                SelfRating = 4,
                ManagerRating = 3,
                CalibratedRating = 4,
                CompletedAt = new DateTimeOffset(2026, 6, 28, 10, 0, 0, TimeSpan.Zero),
            });

            await seed.SaveChangesAsync();
        }

        ReportRunResult run = await RunAsync(provider, "performance");

        Assert.True(run.Succeeded, run.Outcome.ToString());

        IReadOnlyDictionary<string, string?> row = Assert.Single(run.Table!.Rows.Items);

        Assert.Equal("Ada Adeyemi", row["employee"]);
        Assert.Equal("H1 2026", row["cycle"]);
        Assert.Equal("4", row["self"]);
        Assert.Equal("3", row["manager"]);
        Assert.Equal("4", row["calibrated"]);
        Assert.Equal("2026-06-28", row["completedAt"]);
    }

    // Empty, not "0". A rating nobody has given, averaged as zero, would drag a
    // department's score down for being incomplete rather than poor.
    [SkippableFact]
    public async Task AnUngivenRatingIsEmptyRatherThanZero()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid adaId = await HireAsync(provider, "Ada", _engineeringId);

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.PerformanceReviews.Add(new PerformanceReview
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                EmployeeId = adaId,
                Period = "H1 2026",
                Status = ReviewStatus.InProgress,
                DueDate = new DateOnly(2026, 6, 30),
            });

            await seed.SaveChangesAsync();
        }

        IReadOnlyDictionary<string, string?> row =
            Assert.Single((await RunAsync(provider, "performance")).Table!.Rows.Items);

        Assert.Equal(string.Empty, row["self"]);
        Assert.Equal(string.Empty, row["manager"]);
        Assert.Equal(string.Empty, row["calibrated"]);
        Assert.Equal(string.Empty, row["completedAt"]);
    }

    [SkippableFact]
    public async Task TheLearningReportReadsTheEnrolmentRegister()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid adaId = await HireAsync(provider, "Ada", _engineeringId);
        Guid courseId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.Courses.Add(new Course
            {
                Id = courseId,
                TenantId = _tenantId,
                Title = "Fire safety",
                Category = CourseCategory.Compliance,
            });

            await seed.SaveChangesAsync();
        }

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.CourseEnrollments.Add(new CourseEnrollment
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                CourseId = courseId,
                EmployeeId = adaId,
                Status = EnrollmentStatus.InProgress,
                Progress = 40,
                EnrolledOn = new DateOnly(2026, 2, 1),
            });

            await seed.SaveChangesAsync();
        }

        ReportRunResult run = await RunAsync(provider, "learning");

        Assert.True(run.Succeeded, run.Outcome.ToString());

        IReadOnlyDictionary<string, string?> row = Assert.Single(run.Table!.Rows.Items);

        Assert.Equal("Ada Adeyemi", row["employee"]);
        Assert.Equal("Fire safety", row["course"]);

        // The course's category, carried onto the enrolment so a completion-by-category
        // chart needs no second call.
        Assert.Equal("Compliance", row["category"]);
        Assert.Equal("40", row["progress"]);
        Assert.Equal("2026-02-01", row["enrolledAt"]);
    }

    // ── pay ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task PayComesFromTheActiveContract()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid adaId = await HireAsync(provider, "Ada", _engineeringId);

        await ContractAsync(adaId, 4_200_000m, ContractStatus.Active, new DateOnly(2021, 1, 10));

        ReportRunResult run = await RunAsync(provider, "employees", columns: ["salary"]);

        Assert.Equal("4200000", Assert.Single(run.Table!.Rows.Items)["salary"]);
    }

    // Null rather than 0. Somebody whose pay is not recorded has not been recorded as
    // earning nothing, and an average that counts them as zero is wrong.
    [SkippableFact]
    public async Task SomebodyWithNoContractHasNoSalaryRatherThanZero()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);

        ReportRunResult run = await RunAsync(provider, "employees", columns: ["salary"]);

        Assert.Null(Assert.Single(run.Table!.Rows.Items)["salary"]);
    }

    // A draft is a proposal. Reporting it as pay would put an unsigned offer into the
    // payroll column of a spreadsheet.
    [SkippableFact]
    public async Task ADraftContractIsNotPay()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid adaId = await HireAsync(provider, "Ada", _engineeringId);

        await ContractAsync(adaId, 9_000_000m, ContractStatus.Draft, new DateOnly(2026, 1, 1));

        ReportRunResult run = await RunAsync(provider, "employees", columns: ["salary"]);

        Assert.Null(Assert.Single(run.Table!.Rows.Items)["salary"]);
    }

    // The latest active one, because a renewal signed before the old contract ends is
    // the ordinary case and the new terms are the ones that answer "what do they earn".
    [SkippableFact]
    public async Task TheMostRecentActiveContractWins()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid adaId = await HireAsync(provider, "Ada", _engineeringId);

        await ContractAsync(adaId, 3_000_000m, ContractStatus.Active, new DateOnly(2021, 1, 10));
        await ContractAsync(adaId, 5_500_000m, ContractStatus.Active, new DateOnly(2025, 4, 1));

        ReportRunResult run = await RunAsync(provider, "employees", columns: ["salary"]);

        Assert.Equal("5500000", Assert.Single(run.Table!.Rows.Items)["salary"]);
    }

    // The reason the per-column gate exists. The reports screen declares Annual Salary
    // on a report gated by organization.employees, so as specified anybody who can open
    // the staff list reads everyone's pay. Salary is recorded on the contract, so the
    // permission that governs it is operations.contracts.
    //
    // The column goes and the rows stay: this manager still gets the roster.
    [SkippableFact]
    public async Task PayIsNotInTheTableForSomebodyWhoCannotReadContracts()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid adaId = await HireAsync(provider, "Ada", _engineeringId);

        await ContractAsync(adaId, 4_200_000m, ContractStatus.Active, new DateOnly(2021, 1, 10));

        await ActWithLevelAsync(provider, adaId, "ada", "organization.employees");

        ReportRunResult run = await RunAsync(provider, "employees");

        Assert.True(run.Succeeded, run.Outcome.ToString());

        ReportTableDto table = run.Table!;

        Assert.DoesNotContain(
            "salary", table.Columns.Select(column => column.Key), StringComparer.Ordinal);

        // Not in the rows either, not merely absent from the column list.
        Assert.DoesNotContain(
            "salary", Assert.Single(table.Rows.Items).Keys, StringComparer.Ordinal);

        // And the report still works.
        Assert.Contains(
            "fullName", table.Columns.Select(column => column.Key), StringComparer.Ordinal);
    }

    // Named explicitly, to be sure the gate is not only a default.
    [SkippableFact]
    public async Task AskingForPayWithoutContractAccessDoesNotProduceIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid adaId = await HireAsync(provider, "Ada", _engineeringId);

        await ContractAsync(adaId, 4_200_000m, ContractStatus.Active, new DateOnly(2021, 1, 10));

        await ActWithLevelAsync(provider, adaId, "ada", "organization.employees");

        ReportRunResult run = await RunAsync(
            provider, "employees", columns: ["fullName", "salary"]);

        Assert.Equal(["fullName"], run.Table!.Columns.Select(column => column.Key).ToArray());
    }

    // And a filter on it is refused rather than honoured, because filtering a hidden
    // column leaks it by halves: narrow above 5,000,000, count the rows, repeat.
    [SkippableFact]
    public async Task FilteringOnPayWithoutContractAccessIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid adaId = await HireAsync(provider, "Ada", _engineeringId);

        await ActWithLevelAsync(provider, adaId, "ada", "organization.employees");

        ReportRunResult run = await RunAsync(
            provider,
            "employees",
            filters: new Dictionary<string, string?> { ["salary"] = "4200000" });

        Assert.Equal(ReportOutcome.NotFilterable, run.Outcome);
        Assert.Equal("salary", run.Column);
    }

    [SkippableFact]
    public async Task PayIsInTheTableForSomebodyWhoCanReadContracts()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid adaId = await HireAsync(provider, "Ada", _engineeringId);

        await ContractAsync(adaId, 4_200_000m, ContractStatus.Active, new DateOnly(2021, 1, 10));

        await ActWithLevelAsync(
            provider, adaId, "ada", "organization.employees", "operations.contracts");

        ReportRunResult run = await RunAsync(provider, "employees", columns: ["salary"]);

        Assert.True(run.Succeeded, run.Outcome.ToString());
        Assert.Equal("4200000", Assert.Single(run.Table!.Rows.Items)["salary"]);
    }

    private async Task ContractAsync(
        Guid employeeId,
        decimal salary,
        ContractStatus status,
        DateOnly startDate)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Contracts.Add(new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = employeeId,
            Title = "Employment contract",
            ContractType = ContractType.Employment,
            Status = status,
            StartDate = startDate,
            Salary = salary,
            Currency = "NGN",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();
    }

    // A user holding one custom level at All scope over the named modules, so the gate
    // can be tested without the owner's blanket access.
    private async Task ActWithLevelAsync(
        ServiceProvider provider,
        Guid employeeId,
        string handle,
        params string[] modules)
    {
        Guid userId = Guid.NewGuid();
        Guid levelId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.Users.Add(new ApplicationUser
            {
                Id = userId,
                TenantId = _tenantId,
                EmployeeId = employeeId,
                FirstName = handle,
                LastName = "Adeyemi",
                Email = $"{handle}-level@acme.com",
                NormalizedEmail = $"{handle.ToUpperInvariant()}-LEVEL@ACME.COM",
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            seed.AccessLevels.Add(new AccessLevel
            {
                Id = levelId,
                TenantId = _tenantId,
                Name = $"{handle} level",
                Kind = AccessLevelKind.Custom,
                Status = AccessLevelStatus.Active,
                Scope = DataScope.Everything,
                Permissions =
                [
                    .. modules.Select(module => new ModulePermission
                    {
                        Module = module,
                        Access = true,
                        Actions = [PermissionAction.View],
                    }),
                ],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            seed.UserAccessLevels.Add(new UserAccessLevel
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                UserId = userId,
                AccessLevelId = levelId,
                AssignedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();
    }
}
