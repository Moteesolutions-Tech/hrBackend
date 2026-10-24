using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Onboarding;
using Motee.Application.Organisation;
using Motee.Application.Tenancy;
using Motee.Domain.Authorization;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// The owner's account exists before any employee does, and now stays that way.
//
// Creating an employee record with their address used to attach it to their login, which
// was how the founder acquired one. They no longer may: an owner runs the account rather
// than working there, so they are not in the directory, take no leave and sit in nobody's
// reporting line. The linking mechanism survives for ordinary accounts; it skips owners.
[Collection(PostgresCollection.Name)]
public class OnboardingGapsTests(PostgresFixture fixture)
{
    private const string AdminEmail = "admin@acme.com";

    private async Task<(Guid TenantId, Guid UserId, ServiceProvider Provider)> RegisterAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = AdminEmail,
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = registration.TenantId;

        return (registration.TenantId, registration.UserId, provider);
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static async Task<Guid> DepartmentAsync(ServiceProvider provider) =>
        (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
        .Department!.Id;

    private static EmployeeRequest Employee(Guid departmentId, string email, string firstName = "Ada") => new()
    {
        FirstName = firstName,
        LastName = "Okafor",
        Email = email,
        Phone = "08012345678",
        JobTitle = "Head of People",
        DepartmentId = departmentId,
        EmploymentType = EmploymentType.FullTime,
    };

    // Nothing is invented for a new tenant. Adding an employee before a department
    // exists fails with a message that says so, which is the intended behaviour.
    [SkippableFact]
    public async Task ANewTenantStartsWithNoDepartments()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, _, ServiceProvider provider) = await RegisterAsync();
        await using ServiceProvider owned = provider;

        await Resolve<ITenantSetupService>(owned).CompleteAsync(tenantId);

        Assert.Empty(await Resolve<IDepartmentService>(owned).ListAsync());
    }

    [SkippableFact]
    public async Task AddingAnEmployeeBeforeAnyDepartmentIsRefusedClearly()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await RegisterAsync();
        await using ServiceProvider owned = provider;

        EmployeeResult result = await Resolve<IEmployeeService>(owned)
            .CreateAsync(Employee(Guid.NewGuid(), "bola@acme.com"));

        Assert.Equal(EmployeeOutcome.UnknownDepartment, result.Outcome);
    }

    // The inverse of what this file used to assert. Somebody may still want a directory
    // row for the person who signed up — they are a real person at a real company — but
    // it is a separate thing from their login, and the two no longer meet.
    [SkippableFact]
    public async Task CreatingARecordWithTheOwnersAddressDoesNotLinkTheirAccount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid userId, ServiceProvider provider) = await RegisterAsync();
        await using ServiceProvider owned = provider;

        Guid departmentId = await DepartmentAsync(owned);

        EmployeeResult created = await Resolve<IEmployeeService>(owned)
            .CreateAsync(Employee(departmentId, AdminEmail));

        // The record is created — somebody may genuinely want a row for that person —
        // but the owner's login does not become it.
        Assert.True(created.Succeeded, created.Outcome.ToString());

        await using MoteeDbContext context = fixture.CreateContext(tenantId);
        ApplicationUser user = await context.Users.SingleAsync(candidate => candidate.Id == userId);

        Assert.Null(user.EmployeeId);
    }

    // The owner reaches everything through the bypass, so Self scope is moot for them —
    // but the employee-shaped surfaces are genuinely empty, and that is correct rather
    // than a missing record. Pinned so nobody "fixes" it by linking them again.
    [SkippableFact]
    public async Task TheOwnerHasNoEmployeeRecordToActAs()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await RegisterAsync();
        await using ServiceProvider owned = provider;

        Guid departmentId = await DepartmentAsync(owned);

        await Resolve<IEmployeeService>(owned).CreateAsync(Employee(departmentId, AdminEmail));

        Assert.Null(await Resolve<ICurrentEmployee>(owned).IdAsync());
    }


    [SkippableFact]
    public async Task AnUnrelatedEmployeeDoesNotLinkToTheOwner()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid userId, ServiceProvider provider) = await RegisterAsync();
        await using ServiceProvider owned = provider;

        Guid departmentId = await DepartmentAsync(owned);

        await Resolve<IEmployeeService>(owned)
            .CreateAsync(Employee(departmentId, "bola@acme.com", "Bola"));

        await using MoteeDbContext context = fixture.CreateContext(tenantId);

        Assert.Null((await context.Users.SingleAsync(candidate => candidate.Id == userId)).EmployeeId);
    }


    [SkippableFact]
    public async Task AnOrdinaryAccountStillLinks()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, _, ServiceProvider provider) = await RegisterAsync();
        await using ServiceProvider owned = provider;

        Guid departmentId = await DepartmentAsync(owned);
        Guid userId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(tenantId))
        {
            seed.Users.Add(new ApplicationUser
            {
                Id = userId,
                TenantId = tenantId,
                FirstName = "Bola",
                LastName = "Okafor",
                Email = "bola@acme.com",
                NormalizedEmail = "BOLA@ACME.COM",
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        EmployeeResult created = await Resolve<IEmployeeService>(owned)
            .CreateAsync(Employee(departmentId, "bola@acme.com", "Bola"));

        await using MoteeDbContext context = fixture.CreateContext(tenantId);

        Assert.Equal(
            created.Employee!.Id,
            (await context.Users.SingleAsync(candidate => candidate.Id == userId)).EmployeeId);
    }

    // Day one, the whole way through: the owner invites the first person, that person
    // hands in their pack, and somebody has to review it. There is nobody — the joiner
    // has no manager, their department has no head, and the owner is no longer an
    // employee who could stand in. What happens here is what every new customer meets.
    //
    // Nobody is blocked by it. The joiner finishes signing up, because invitation
    // acceptance discards this outcome on purpose, and HR can still complete the
    // record, because one with no chain counts as approved. The cost is that the
    // review silently does not happen, and the result looks identical to a record HR
    // typed in themselves — which needs no review.
    //
    // Asserts the current behaviour on purpose, so the gap is visible and so this turns
    // red when somebody changes it. It is not the behaviour we want.
    [SkippableFact]
    public async Task TheFirstInvitedJoinersPackIsNeverReviewed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await RegisterAsync();
        await using ServiceProvider owned = provider;

        Guid departmentId = await DepartmentAsync(owned);

        // The seeded chain, not one the test wrote: LineManager required, department
        // head optional. Substituting a friendlier chain here would be testing the
        // substitute.
        EmployeeResult hired = await Resolve<IEmployeeService>(owned)
            .CreateAsync(Employee(departmentId, "bola@acme.com", "Bola"), OnboardingMethod.Invite);

        Assert.True(hired.Succeeded, hired.Outcome.ToString());

        IOnboardingService onboarding = Resolve<IOnboardingService>(owned);

        OnboardingResult submitted = await onboarding.SubmitAsync(hired.Employee!.Id);

        Assert.Equal(OnboardingOutcome.ReviewUnstartable, submitted.Outcome);

        // Submitted all the same — the record was saved before the failure was returned,
        // which is why the second attempt is refused as a duplicate rather than retried.
        Assert.Equal(
            OnboardingOutcome.NotAllowed,
            (await onboarding.SubmitAsync(hired.Employee!.Id)).Outcome);

        Guid recordId = (await onboarding.EnsureAsync(hired.Employee!.Id)).Id;

        OnboardingResult completed = await Resolve<IOnboardingService>(owned)
            .CompleteAsync(recordId);

        // Still completable, and still no review. Nobody is blocked by this, which is
        // deliberate — the joiner cannot fix their company's reporting lines.
        Assert.True(completed.Succeeded, completed.Outcome.ToString());
        Assert.Null(completed.Record!.Review);

        // But it no longer passes for a record that never needed reviewing. The reason
        // the engine worked out is on the record, in the words it wrote, naming the
        // thing to fix rather than the category it fell into.
        Assert.Contains(
            "line manager",
            completed.Record.ReviewUnavailableReason!,
            StringComparison.OrdinalIgnoreCase);
    }

    // The other half: a record HR typed in themselves wants no review and must not be
    // marked as missing one. Both come back with a null Review, so this is the only
    // thing separating "nothing to do" from "nobody looked".
    [SkippableFact]
    public async Task ARecordThatNeverNeededAReviewCarriesNoReason()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await RegisterAsync();
        await using ServiceProvider owned = provider;

        Guid departmentId = await DepartmentAsync(owned);

        EmployeeResult hired = await Resolve<IEmployeeService>(owned)
            .CreateAsync(Employee(departmentId, "bola@acme.com", "Bola"), OnboardingMethod.Manual);

        OnboardingResult submitted = await Resolve<IOnboardingService>(owned)
            .SubmitAsync(hired.Employee!.Id);

        Assert.True(submitted.Succeeded, submitted.Outcome.ToString());
        Assert.Null(submitted.Record!.Review);
        Assert.Null(submitted.Record.ReviewUnavailableReason);
    }

    // One address, one employee. Untouched by the owner rule: the refusal comes from the
    // employee table, not from who is linked to what.
    [SkippableFact]
    public async Task ASecondRecordForTheSameAddressIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await RegisterAsync();
        await using ServiceProvider owned = provider;

        Guid departmentId = await DepartmentAsync(owned);

        await Resolve<IEmployeeService>(owned).CreateAsync(Employee(departmentId, AdminEmail));

        EmployeeResult second = await Resolve<IEmployeeService>(owned)
            .CreateAsync(Employee(departmentId, AdminEmail, "Someone"));

        Assert.Equal(EmployeeOutcome.DuplicateEmail, second.Outcome);
    }
}
