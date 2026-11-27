using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// An employee asking for their own details to be corrected, and HR deciding.
//
// The behaviour that matters most is that approving writes the field. What this
// replaces kept requests in a JSON file on the web server and applied an approved
// change as a client-side override laid over the employee it had loaded — so the record
// never moved, and the next person to open the profile saw the old value. Several tests
// below exist only to pin that the record actually changes.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class ChangeRequestServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _adaId;
    private Guid _adaUserId;
    private Guid _benId;
    private Guid _benUserId;
    private Guid _hrUserId;

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
        _hrUserId = registration.UserId;

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _adaId = await HireAsync(provider, "Ada");
        _benId = await HireAsync(provider, "Ben");

        _adaUserId = await AccountAsync(_adaId, "ada");
        _benUserId = await AccountAsync(_benId, "ben");

        ActAs(provider, _adaUserId);

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IChangeRequestService Service(ServiceProvider provider) =>
        Resolve<IChangeRequestService>(provider);

    private static void ActAs(ServiceProvider provider, Guid userId) =>
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();

    private async Task<Guid> HireAsync(ServiceProvider provider, string first) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Adeyemi",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2021, 1, 10),
        })).Employee!.Id;

    private async Task<Guid> AccountAsync(Guid employeeId, string handle)
    {
        Guid userId = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

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
            CreatedAt = _clock.GetUtcNow(),
        });

        await seed.SaveChangesAsync();

        return userId;
    }

    private static RaiseChangeRequest Asking(
        string field = "preferredName",
        string? value = "Addie",
        string reason = "Everyone calls me this.") =>
        new() { Field = field, RequestedValue = value, Reason = reason };

    private async Task<Employee> AdaAsync()
    {
        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        return await read.Employees.AsNoTracking().FirstAsync(row => row.Id == _adaId);
    }

    // ── the fields on offer ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheFieldListCarriesLabelsAndCurrentValues()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IReadOnlyList<RequestableFieldDto> fields = await Service(provider).FieldsAsync();

        RequestableFieldDto phone = fields.First(field => field.Key == "phone");

        Assert.Equal("Phone number", phone.Label);
        Assert.Equal(ProfileFieldGroup.Contact, phone.Group);
        Assert.Equal("08012345678", phone.CurrentValue);

        // Bank and identity sit on their own blocks, which do not exist yet for a new
        // hire. Reading them is empty rather than a failure, and reading must not
        // create the rows — see the next test.
        Assert.Contains(fields, field => field.Key == "accountNumber");
        Assert.Null(fields.First(field => field.Key == "accountNumber").CurrentValue);
    }

    // Drawing a form must not write to the database. Without this, every employee who
    // ever opened the page would leave an empty bank-details row behind.
    [SkippableFact]
    public async Task ReadingTheFieldListCreatesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await Service(provider).FieldsAsync();

        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        Assert.Empty(read.EmployeeBankDetails);
        Assert.Empty(read.EmployeeIdentityDocuments);
    }

    // ── raising ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ARequestRecordsWhatWasAskedAndWhatWasThere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ChangeRequestResult raised = await Service(provider).RaiseAsync(
            Asking("phone", "08099999999", "New number."));

        Assert.True(raised.Succeeded, $"{raised.Outcome} / {raised.Reason}");

        ChangeRequestDto request = raised.Request!;

        Assert.Equal(_adaId, request.EmployeeId);
        Assert.Equal("phone", request.Field);
        Assert.Equal("Phone number", request.Label);
        Assert.Equal("08099999999", request.RequestedValue);

        // The snapshot: what Ada was looking at when she said it was wrong.
        Assert.Equal("08012345678", request.CurrentValue);
        Assert.Equal(ChangeRequestStatus.Pending, request.Status);
        Assert.Equal("Ada Adeyemi", request.EmployeeName);
    }

    // The mass-assignment case. The screen names fields as dot-paths, so the obvious
    // implementation reflects the path onto the entity — and then a request for
    // "status" approves somebody off probation.
    [SkippableTheory]
    [InlineData("status")]
    [InlineData("managerId")]
    [InlineData("departmentId")]
    [InlineData("startDate")]
    [InlineData("email")]
    [InlineData("salary")]
    public async Task AFieldOutsideTheCatalogueIsRefused(string field)
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ChangeRequestResult refused = await Service(provider).RaiseAsync(
            Asking(field, "Active", "Please."));

        Assert.Equal(ChangeRequestOutcome.FieldNotRequestable, refused.Outcome);
    }

    [SkippableFact]
    public async Task ARequestForTheValueAlreadyThereIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ChangeRequestResult refused = await Service(provider).RaiseAsync(
            Asking("phone", "08012345678", "No change at all."));

        // Otherwise the queue fills with requests whose approval changes nothing, and
        // HR has to open each one to find that out.
        Assert.Equal(ChangeRequestOutcome.NoChange, refused.Outcome);
    }

    [SkippableFact]
    public async Task ASecondRequestOnTheSameFieldIsRefusedWhileOneIsPending()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await Service(provider).RaiseAsync(Asking())).Succeeded);

        ChangeRequestResult second = await Service(provider).RaiseAsync(
            Asking(value: "Ada-Marie"));

        // Two would leave the queue with the same decision to make twice, and whichever
        // was approved second would silently overwrite the first.
        Assert.Equal(ChangeRequestOutcome.AlreadyPending, second.Outcome);
    }

    [SkippableFact]
    public async Task ARequestWithNoReasonIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            ChangeRequestOutcome.ReasonRequired,
            (await Service(provider).RaiseAsync(Asking(reason: "   "))).Outcome);
    }

    // The account owner has no employee record and never will — they run the company's
    // account rather than working there, so they have no details to change.
    [SkippableFact]
    public async Task AnAccountWithNoEmployeeRecordCannotRaiseOne()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ActAs(provider, _hrUserId);

        Assert.Equal(
            ChangeRequestOutcome.NoEmployeeRecord,
            (await Service(provider).RaiseAsync(Asking())).Outcome);
    }

    // ── approving writes the record ─────────────────────────────────────────

    [SkippableFact]
    public async Task ApprovingWritesTheFieldToTheEmployee()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Service(provider).RaiseAsync(Asking())).Request!.Id;

        ActAs(provider, _hrUserId);

        ChangeRequestResult approved = await Service(provider).ApproveAsync(id, "Confirmed.");

        Assert.True(approved.Succeeded, $"{approved.Outcome} / {approved.Reason}");
        Assert.Equal(ChangeRequestStatus.Approved, approved.Request!.Status);
        Assert.Equal("Owner Okafor", approved.Request.DecidedByName);

        // The point of the whole feature. What this replaced recorded the decision and
        // left the record untouched.
        Assert.Equal("Addie", (await AdaAsync()).PreferredName);
    }

    [SkippableFact]
    public async Task ApprovingWritesAFieldOnAProfileBlock()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Service(provider).RaiseAsync(
            Asking("accountNumber", "0123456789", "Changed banks."))).Request!.Id;

        ActAs(provider, _hrUserId);

        Assert.True((await Service(provider).ApproveAsync(id)).Succeeded);

        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        EmployeeBankDetails bank = await read.EmployeeBankDetails
            .AsNoTracking()
            .FirstAsync(row => row.EmployeeId == _adaId);

        // The leading zero survives, which is why these are stored as text.
        Assert.Equal("0123456789", bank.AccountNumber);
    }

    [SkippableFact]
    public async Task RejectingLeavesTheRecordAloneAndKeepsTheReason()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Service(provider).RaiseAsync(Asking())).Request!.Id;

        ActAs(provider, _hrUserId);

        ChangeRequestResult rejected = await Service(provider)
            .RejectAsync(id, "Send a deed poll first.");

        Assert.True(rejected.Succeeded, $"{rejected.Outcome} / {rejected.Reason}");
        Assert.Equal(ChangeRequestStatus.Rejected, rejected.Request!.Status);
        Assert.Equal("Send a deed poll first.", rejected.Request.DecisionNote);

        Assert.Null((await AdaAsync()).PreferredName);
    }

    // Being told no without being told why sends somebody to HR to ask, which is the
    // cost the field exists to avoid.
    [SkippableFact]
    public async Task RejectingWithoutAReasonIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Service(provider).RaiseAsync(Asking())).Request!.Id;

        ActAs(provider, _hrUserId);

        Assert.Equal(
            ChangeRequestOutcome.NoteRequired,
            (await Service(provider).RejectAsync(id, "  ")).Outcome);
    }

    [SkippableFact]
    public async Task DecidingTwiceIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Service(provider).RaiseAsync(Asking())).Request!.Id;

        ActAs(provider, _hrUserId);

        Assert.True((await Service(provider).ApproveAsync(id)).Succeeded);

        // A second decision would record two people ruling on one thing.
        Assert.Equal(
            ChangeRequestOutcome.AlreadyDecided,
            (await Service(provider).RejectAsync(id, "Changed my mind.")).Outcome);
    }

    // A value that cannot go in the column is caught at approval, which is the moment
    // it would otherwise reach the record — and the request stays pending, because a
    // refused value is something to go back to the employee about.
    [SkippableFact]
    public async Task ApprovingAValueTheRecordRefusesLeavesItPending()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Service(provider).RaiseAsync(
            Asking("dateOfBirth", "2099-01-01", "Fixing my birthday."))).Request!.Id;

        ActAs(provider, _hrUserId);

        ChangeRequestResult refused = await Service(provider).ApproveAsync(id);

        Assert.Equal(ChangeRequestOutcome.ValueRefused, refused.Outcome);

        // The sentence, not the category: whoever has to fix it needs to know what was
        // wrong with the value.
        Assert.Equal("Date of birth cannot be in the future.", refused.Reason);

        IReadOnlyList<ChangeRequestDto> mine = await Service(provider)
            .ForEmployeeAsync(_adaId);

        Assert.Equal(ChangeRequestStatus.Pending, Assert.Single(mine).Status);
    }

    // ── withdrawing ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheRequesterCanWithdrawTheirOwn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Service(provider).RaiseAsync(Asking())).Request!.Id;

        ChangeRequestResult withdrawn = await Service(provider).WithdrawAsync(id);

        Assert.True(withdrawn.Succeeded, $"{withdrawn.Outcome} / {withdrawn.Reason}");

        // Distinct from rejected: the log should not read as though HR refused
        // something nobody ever ruled on.
        Assert.Equal(ChangeRequestStatus.Withdrawn, withdrawn.Request!.Status);
    }

    [SkippableFact]
    public async Task SomebodyElseCannotWithdrawIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Service(provider).RaiseAsync(Asking())).Request!.Id;

        ActAs(provider, _benUserId);

        Assert.Equal(
            ChangeRequestOutcome.NotTheRequester,
            (await Service(provider).WithdrawAsync(id)).Outcome);
    }

    [SkippableFact]
    public async Task WithdrawingFreesTheFieldForAnotherRequest()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Service(provider).RaiseAsync(Asking())).Request!.Id;

        Assert.True((await Service(provider).WithdrawAsync(id)).Succeeded);

        // Only a pending one blocks, so a withdrawal lets somebody try again with a
        // different value.
        Assert.True((await Service(provider).RaiseAsync(Asking(value: "Ada-Marie"))).Succeeded);
    }

    // ── the queue and the log ───────────────────────────────────────────────

    [SkippableFact]
    public async Task TheQueueShowsPendingRequestsOldestFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid first = (await Service(provider).RaiseAsync(Asking())).Request!.Id;

        _clock.Advance(TimeSpan.FromHours(2));

        ActAs(provider, _benUserId);

        Guid second = (await Service(provider).RaiseAsync(
            Asking("phone", "08077777777", "New number."))).Request!.Id;

        ActAs(provider, _hrUserId);

        PagedResult<ChangeRequestDto> queue = await Service(provider)
            .ListAsync(new ChangeRequestQuery());

        Assert.Equal(2, queue.TotalItems);

        // A queue is worked from the end that has been waiting longest — the opposite
        // of the submissions register, which is read newest first.
        Assert.Equal(first, queue.Items[0].Id);
        Assert.Equal(second, queue.Items[1].Id);
    }

    [SkippableFact]
    public async Task ADecidedRequestLeavesTheQueueButStaysInTheLog()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Service(provider).RaiseAsync(Asking())).Request!.Id;

        ActAs(provider, _hrUserId);
        Assert.True((await Service(provider).ApproveAsync(id)).Succeeded);

        Assert.Empty((await Service(provider).ListAsync(new ChangeRequestQuery())).Items);

        // "What happened to the change I asked for" is the question the log answers,
        // and the answer is usually something already decided.
        ChangeRequestDto logged = Assert.Single(
            await Service(provider).ForEmployeeAsync(_adaId));

        Assert.Equal(ChangeRequestStatus.Approved, logged.Status);
        Assert.Equal("Ada Adeyemi", logged.EmployeeName);
        Assert.Equal("ada Adeyemi", logged.RequestedByName);
    }

    [SkippableFact]
    public async Task MyLogShowsOnlyMyOwnRequests()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await Service(provider).RaiseAsync(Asking())).Succeeded);

        ActAs(provider, _benUserId);

        Assert.Empty(await Service(provider).MineAsync());

        Assert.True((await Service(provider).RaiseAsync(
            Asking("phone", "08077777777", "New number."))).Succeeded);

        ChangeRequestDto only = Assert.Single(await Service(provider).MineAsync());

        Assert.Equal(_benId, only.EmployeeId);
    }
}
