using System.Net;
using Motee.Domain.Attendance;
using Motee.Domain.Common;
using Motee.Domain.Contracts;
using Motee.Domain.Documents;
using Motee.Domain.Employees;
using Motee.Domain.Expenses;
using Motee.Domain.Leave;
using Motee.Domain.Organisation;
using Motee.Domain.Performance;
using Motee.Domain.Settings;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Integration;

namespace Motee.Tests.Api;

// Reading one record by its id.
//
// The registers were closed by requiring more than Self scope on the organisation-wide
// lists. This is the same leak reached a different way, and closing the lists may have
// made it less visible rather than less real: an ordinary employee still held the
// module permission, and a read of one record by id did no narrowing at all — so
// GET /contracts/{id} returned any colleague's contract, salary included.
//
// Guids are not guessable, but ids travel: in links somebody pastes, in logs, and in
// the responses of other endpoints. "Hard to guess" is not an access rule.
//
// Bola holds no access level. Everything below belongs to Ada, and none of it is
// Bola's to read.
[Collection(PostgresCollection.Name)]
public class SingleRecordReachTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _bolaId;
    private Guid _adaId;
    private Guid _bolaUserId;

    private Guid _contractId;
    private Guid _leaveId;
    private Guid _claimId;
    private Guid _reviewId;
    private Guid _goalId;
    private Guid _timesheetId;
    private Guid _documentId;
    private Guid _handbookId;

    private async Task<ApiFactory> ArrangeAsync()
    {
        await fixture.ResetAsync();

        _tenantId = Guid.NewGuid();
        _bolaUserId = Guid.NewGuid();
        _bolaId = Guid.NewGuid();
        _adaId = Guid.NewGuid();

        _contractId = Guid.NewGuid();
        _leaveId = Guid.NewGuid();
        _claimId = Guid.NewGuid();
        _reviewId = Guid.NewGuid();
        _goalId = Guid.NewGuid();
        _timesheetId = Guid.NewGuid();
        _documentId = Guid.NewGuid();
        _handbookId = Guid.NewGuid();

        Guid departmentId = Guid.NewGuid();
        Guid leaveTypeId = Guid.NewGuid();
        Guid folderId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = _tenantId,
                Name = "Acme",
                Slug = $"acme-{_tenantId:N}",
                CountryCode = CountryCode.Nigeria,

                // Every feature on, so a refusal can only come from the permission
                // layer rather than from a module being switched off.
                Settings = new TenantSettings
                {
                    EnabledModules = [.. FeatureCatalogue.All.Select(feature => feature.Id)],
                },
            });

            await seed.SaveChangesAsync();
        }

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.Departments.Add(new Department
            {
                Id = departmentId,
                TenantId = _tenantId,
                Name = "Engineering",
                Code = "ENG",
            });

            seed.LeaveTypes.Add(new LeaveType
            {
                Id = leaveTypeId,
                TenantId = _tenantId,
                Name = "Annual",
                Code = "ANN",
            });

            Add(seed, _bolaId, "Bola", departmentId);
            Add(seed, _adaId, "Ada", departmentId);

            seed.Users.Add(new ApplicationUser
            {
                Id = _bolaUserId,
                TenantId = _tenantId,
                FirstName = "Bola",
                LastName = "Okafor",
                Email = "bola@acme.com",
                UserName = "bola@acme.com",
                NormalizedEmail = "BOLA@ACME.COM",
                EmployeeId = _bolaId,
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        // A second save: everything below points at the employees above by foreign key,
        // and EF orders inserts within one SaveChanges by its own graph walk.
        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.Contracts.Add(new Contract
            {
                Id = _contractId,
                TenantId = _tenantId,
                EmployeeId = _adaId,
                Title = "Ada's employment contract",
                ContractType = ContractType.Employment,
                Status = ContractStatus.Active,
                StartDate = new DateOnly(2021, 1, 10),
                Salary = 7_654_321m,
                Currency = "NGN",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            seed.LeaveRequests.Add(new LeaveRequest
            {
                Id = _leaveId,
                TenantId = _tenantId,
                EmployeeId = _adaId,
                LeaveTypeId = leaveTypeId,
                StartDate = new DateOnly(2026, 3, 2),
                EndDate = new DateOnly(2026, 3, 6),
                TotalDays = 5,
                Status = LeaveRequestStatus.Approved,
                Reason = "Chemotherapy appointments",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            seed.ExpenseClaims.Add(new ExpenseClaim
            {
                Id = _claimId,
                TenantId = _tenantId,
                EmployeeId = _adaId,
                Reference = "EXP-0001",
                Title = "Ada's taxi to the clinic",
                Merchant = "City Cabs",

                // Positive: a check constraint on the table insists, which is right —
                // a claim for nothing is not a claim.
                Amount = 4_500m,
                Status = ExpenseStatus.Submitted,
                Currency = "NGN",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            seed.PerformanceReviews.Add(new PerformanceReview
            {
                Id = _reviewId,
                TenantId = _tenantId,
                EmployeeId = _adaId,
                Period = "H1 2026",
                Status = ReviewStatus.Completed,
                DueDate = new DateOnly(2026, 6, 30),
                ManagerRating = 2,
                Strengths = "Ada is struggling with deadlines",
            });

            seed.PerformanceGoals.Add(new PerformanceGoal
            {
                Id = _goalId,
                TenantId = _tenantId,
                EmployeeId = _adaId,
                Title = "Ada's improvement plan",
                DueDate = new DateOnly(2026, 9, 30),
            });

            seed.Timesheets.Add(new Timesheet
            {
                Id = _timesheetId,
                TenantId = _tenantId,
                EmployeeId = _adaId,
                WeekStart = new DateOnly(2026, 3, 2),
                Status = TimesheetStatus.Submitted,
            });

            seed.DocumentFolders.Add(new DocumentFolder
            {
                Id = folderId,
                TenantId = _tenantId,
                Name = "HR filing",
                Type = FolderType.Custom,
            });

            await seed.SaveChangesAsync();
        }

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            // Ada's own upload: assigned to nobody and shared with nobody, which is
            // what keeps an upload out of the company's filing until HR accepts it.
            seed.HrDocuments.Add(new HrDocument
            {
                Id = _documentId,
                TenantId = _tenantId,
                Name = "Ada's medical note",
                Category = DocumentCategory.Identity,
                FolderId = folderId,
                Assignment = AssignmentScope.Specific,
                UploadedByEmployeeId = _adaId,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            // Aimed at the whole company, so everybody may read it. The rule has to
            // separate this from the one above.
            seed.HrDocuments.Add(new HrDocument
            {
                Id = _handbookId,
                TenantId = _tenantId,
                Name = "Employee handbook",
                Category = DocumentCategory.Handbook,
                FolderId = folderId,
                Assignment = AssignmentScope.Global,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        return new ApiFactory(fixture.ConnectionString!)
        {
            ActingAs = _bolaUserId,
            ActingTenant = _tenantId,
            ActingEmployee = _bolaId,
        };
    }

    private void Add(MoteeDbContext seed, Guid employeeId, string first, Guid departmentId) =>
        seed.Employees.Add(new Employee
        {
            Id = employeeId,
            TenantId = _tenantId,
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            DepartmentId = departmentId,
            Status = EmployeeStatus.Active,
        });

    // The disclosure, as plainly as it can be put: a colleague's salary, from a request
    // any employee can make if they have the id.
    [SkippableFact]
    public async Task AnOrdinaryEmployeeCannotReadAColleaguesContractById()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient()
            .GetAsync($"/api/v1/contracts/{_contractId:D}");

        string body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("7654321", body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task AnOrdinaryEmployeeCannotReadAColleaguesLeaveRequestById()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient()
            .GetAsync($"/api/v1/leave/requests/{_leaveId:D}");

        string body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Chemotherapy", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task AnOrdinaryEmployeeCannotReadAColleaguesReviewById()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient()
            .GetAsync($"/api/v1/performance/reviews/{_reviewId:D}");

        string body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("struggling", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Not found rather than forbidden, matching the guarantors route: telling somebody
    // an id exists but is none of their business is itself a fact about the record.
    [SkippableTheory]
    [InlineData("expenses/claims")]
    [InlineData("performance/goals")]
    [InlineData("attendance/timesheets")]
    public async Task AnOrdinaryEmployeeCannotReadAColleaguesRecordById(string path)
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        Guid id = path switch
        {
            "expenses/claims" => _claimId,
            "performance/goals" => _goalId,
            _ => _timesheetId,
        };

        HttpResponseMessage response = await api.CreateClient()
            .GetAsync($"/api/v1/{path}/{id:D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Somebody else's balances, by their employee id rather than a record id. The same
    // hole with a different key.
    [SkippableFact]
    public async Task AnOrdinaryEmployeeCannotReadAColleaguesLeaveBalances()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient()
            .GetAsync($"/api/v1/leave/balances/{_adaId:D}");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    // ── documents and approvals, which have rules of their own ──────────────

    // A document is not an employee's record, so the employee-reach rule does not apply
    // on its own: this one is assigned to nobody and shared with nobody, which is what
    // keeps an upload out of the company's filing until HR accepts it.
    [SkippableFact]
    public async Task AnOrdinaryEmployeeCannotReadAColleaguesDocumentById()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient()
            .GetAsync($"/api/v1/documents/{_documentId:D}");

        string body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Ada's medical note", body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The acknowledgement report names who has agreed and who has not, so it is no more
    // readable than the document it is about.
    [SkippableFact]
    public async Task AnOrdinaryEmployeeCannotReadAColleaguesDocumentAcknowledgements()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient()
            .GetAsync($"/api/v1/documents/{_documentId:D}/acknowledgements");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    // The company handbook is aimed at everybody, so it stays readable — the rule has to
    // separate "assigned to the whole company" from "somebody else's record", or
    // self-service loses the documents it exists to show.
    [SkippableFact]
    public async Task AnOrdinaryEmployeeCanStillReadACompanyWideDocument()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient()
            .GetAsync($"/api/v1/documents/{_handbookId:D}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // And the other half: their own records stay readable, or the fix would have simply
    // closed self-service out of its own screens.
    [SkippableFact]
    public async Task AnEmployeeCanStillReadTheirOwnLeaveBalances()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient()
            .GetAsync($"/api/v1/leave/balances/{_bolaId:D}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
