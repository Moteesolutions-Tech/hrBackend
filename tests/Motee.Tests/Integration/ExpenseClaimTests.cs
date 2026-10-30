using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Expenses;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Expenses;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// Claims, filed and reimbursed.
//
// Three things these hold. The lifecycle, because a claim that can be edited while
// somebody is approving it means they agree one figure and the company pays another.
// The reference, because it is what a claim is chased by and reissuing one makes two
// claims out of one. And the money itself — stored exactly, never converted, never
// rounded through a float.
[Collection(PostgresCollection.Name)]
public class ExpenseClaimTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _employeeId;
    private Guid _managerId;

    private async Task<ServiceProvider> ArrangeAsync(string country = "NG")
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = "ada@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = country,
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        Guid departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _managerId = await HireAsync(provider, departmentId, "Cara", "cara@acme.com");
        _employeeId = await HireAsync(provider, departmentId, "Bola", "bola@acme.com", _managerId);

        // The approver needs a login, or the chain resolves to nobody and every
        // submission fails for a reason unrelated to what is being tested.
        await GiveAccountAsync(_managerId, "cara");

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            (await GiveAccountAsync(_employeeId, "bola")).ToString();

        return provider;
    }

    private async Task<Guid> GiveAccountAsync(Guid employeeId, string handle)
    {
        Guid userId = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
        {
            Id = userId,
            TenantId = _tenantId,
            EmployeeId = employeeId,
            FirstName = handle,
            LastName = "Okafor",
            Email = $"{handle}-login@acme.com",
            NormalizedEmail = $"{handle.ToUpperInvariant()}-LOGIN@ACME.COM",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();

        return userId;
    }

    private static async Task<Guid> HireAsync(
        ServiceProvider provider,
        Guid departmentId,
        string first,
        string email,
        Guid? managerId = null) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = email,
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId,
            ManagerId = managerId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
        })).Employee!.Id;

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static ExpenseClaimRequest Claim(
        decimal amount = 42.50m,
        string? currency = null,
        int incurredDaysAgo = 3) => new()
    {
        Title = "Taxi to client site",
        Category = ExpenseCategory.Travel,
        Amount = amount,
        Currency = currency,
        Merchant = "Bolt",
        IncurredOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-incurredDaysAgo),
        Notes = "Gateway kickoff.",
    };

    private static async Task<Guid> FileAsync(ServiceProvider provider, ExpenseClaimRequest request)
    {
        ExpenseResult created = await Resolve<IExpenseClaimService>(provider)
            .CreateAsync(request);

        Assert.True(created.Succeeded, created.Outcome.ToString());

        return created.Claim!.Id;
    }

    // Filing and submitting are separate acts — otherwise there is no way to save a
    // half-written claim.
    [SkippableFact]
    public async Task ANewClaimIsADraftWithNoReference()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ExpenseClaimDto claim = (await Resolve<IExpenseClaimService>(provider)
            .CreateAsync(Claim())).Claim!;

        Assert.Equal(ExpenseStatus.Draft, claim.Status);
        Assert.Null(claim.Reference);
        Assert.Null(claim.Review);
        Assert.False(claim.Returned);
    }

    // The company's own currency unless the claim says otherwise. Nigeria's is NGN.
    [SkippableFact]
    public async Task ACurrencyIsTakenFromTheCompanyWhenNotGiven()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            "NGN",
            (await Resolve<IExpenseClaimService>(provider).CreateAsync(Claim())).Claim!.Currency);
    }

    [SkippableFact]
    public async Task AUkCompanyFilesInPounds()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(country: "GB");

        Assert.Equal(
            "GBP",
            (await Resolve<IExpenseClaimService>(provider).CreateAsync(Claim())).Claim!.Currency);
    }

    // Spending abroad is the exception worth typing, and it is stored as spent
    // rather than converted — a rate belongs to the day it was applied.
    [SkippableFact]
    public async Task AForeignCurrencyIsKeptAsFiled()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            "USD",
            (await Resolve<IExpenseClaimService>(provider)
                .CreateAsync(Claim(currency: "usd"))).Claim!.Currency);
    }

    // Money, stored exactly. A float cannot hold this and the error compounds the
    // moment anything is summed.
    [SkippableFact]
    public async Task AnAmountSurvivesTheRoundTripExactly()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await FileAsync(provider, Claim(amount: 1234.5678m));

        Assert.Equal(
            1234.5678m,
            (await Resolve<IExpenseClaimService>(provider).GetAsync(id))!.Amount);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task AClaimForNothingOrLessIsRefused(int amount)
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            ExpenseOutcome.AmountNotPositive,
            (await Resolve<IExpenseClaimService>(provider)
                .CreateAsync(Claim(amount: amount))).Outcome);
    }

    [SkippableFact]
    public async Task MoneySpentInTheFutureIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            ExpenseOutcome.IncurredInTheFuture,
            (await Resolve<IExpenseClaimService>(provider)
                .CreateAsync(Claim(incurredDaysAgo: -2))).Outcome);
    }

    [SkippableFact]
    public async Task ACurrencyThatIsNotThreeLettersIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            ExpenseOutcome.CurrencyInvalid,
            (await Resolve<IExpenseClaimService>(provider)
                .CreateAsync(Claim(currency: "Pounds"))).Outcome);
    }

    // Submitting hands it to the seeded expense chain and issues the reference —
    // the first moment anybody else sees the claim and has something to quote.
    [SkippableFact]
    public async Task SubmittingStartsTheChainAndIssuesAReference()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IExpenseClaimService claims = Resolve<IExpenseClaimService>(provider);

        ExpenseResult submitted = await claims.SubmitAsync(await FileAsync(provider, Claim()));

        Assert.True(submitted.Succeeded, $"{submitted.Outcome}: {submitted.Reason}");
        Assert.Equal(ExpenseStatus.Submitted, submitted.Claim!.Status);
        Assert.NotNull(submitted.Claim.Review);
        Assert.StartsWith(
            $"EXP-{DateTime.UtcNow.Year}-", submitted.Claim.Reference!, StringComparison.Ordinal);
    }

    // Per year, continuing from the highest issued.
    [SkippableFact]
    public async Task ReferencesRunInSequence()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IExpenseClaimService claims = Resolve<IExpenseClaimService>(provider);

        string first = (await claims.SubmitAsync(await FileAsync(provider, Claim())))
            .Claim!.Reference!;
        string second = (await claims.SubmitAsync(await FileAsync(provider, Claim())))
            .Claim!.Reference!;

        Assert.Equal($"EXP-{DateTime.UtcNow.Year}-00001", first);
        Assert.Equal($"EXP-{DateTime.UtcNow.Year}-00002", second);
    }

    // A claim that changed its reference halfway through would be two claims in
    // every email about it.
    [SkippableFact]
    public async Task WithdrawingAndResubmittingKeepsTheSameReference()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IExpenseClaimService claims = Resolve<IExpenseClaimService>(provider);
        Guid id = await FileAsync(provider, Claim());

        string issued = (await claims.SubmitAsync(id)).Claim!.Reference!;

        ExpenseResult withdrawn = await claims.WithdrawAsync(id);

        Assert.Equal(ExpenseStatus.Draft, withdrawn.Claim!.Status);
        Assert.Equal(issued, withdrawn.Claim.Reference);

        Assert.Equal(issued, (await claims.SubmitAsync(id)).Claim!.Reference);
    }

    // Changing the amount under a reviewer means they agree one figure and the
    // company pays another.
    [SkippableFact]
    public async Task ASubmittedClaimCannotBeEdited()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IExpenseClaimService claims = Resolve<IExpenseClaimService>(provider);
        Guid id = await FileAsync(provider, Claim());

        await claims.SubmitAsync(id);

        ExpenseResult refused = await claims.UpdateAsync(id, Claim(amount: 9999m));

        Assert.Equal(ExpenseOutcome.NotAllowed, refused.Outcome);
        Assert.Contains("no longer a draft", refused.Reason!, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ADraftCannotBeSubmittedTwice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IExpenseClaimService claims = Resolve<IExpenseClaimService>(provider);
        Guid id = await FileAsync(provider, Claim());

        await claims.SubmitAsync(id);

        Assert.Equal(ExpenseOutcome.NotAllowed, (await claims.SubmitAsync(id)).Outcome);
    }

    // Sent back to be rewritten, not turned down. The flag is what lets the screen
    // say "sent back" on something that is otherwise an ordinary editable draft.
    [SkippableFact]
    public async Task ReturningAClaimReopensItAsADraftWithTheReason()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IExpenseClaimService claims = Resolve<IExpenseClaimService>(provider);
        Guid id = await FileAsync(provider, Claim());

        await claims.SubmitAsync(id);

        ExpenseClaimDto returned = (await claims.ReturnAsync(id, "No receipt attached.")).Claim!;

        Assert.Equal(ExpenseStatus.Draft, returned.Status);
        Assert.True(returned.Returned);
        Assert.Equal("No receipt attached.", returned.ReturnedReason);

        // And it is editable again, which is the point of returning rather than
        // rejecting.
        Assert.True((await claims.UpdateAsync(id, Claim(amount: 50m))).Succeeded);
    }

    // Or the corrected claim still shows why it was sent back and reads as though
    // it had been returned a second time.
    [SkippableFact]
    public async Task ResubmittingClearsTheReturnedFlag()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IExpenseClaimService claims = Resolve<IExpenseClaimService>(provider);
        Guid id = await FileAsync(provider, Claim());

        await claims.SubmitAsync(id);
        await claims.ReturnAsync(id, "No receipt attached.");

        ExpenseClaimDto again = (await claims.SubmitAsync(id)).Claim!;

        Assert.False(again.Returned);
        Assert.Null(again.ReturnedReason);
    }

    [SkippableFact]
    public async Task ReturningNeedsAReason()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IExpenseClaimService claims = Resolve<IExpenseClaimService>(provider);
        Guid id = await FileAsync(provider, Claim());

        await claims.SubmitAsync(id);

        Assert.Equal(ExpenseOutcome.ReasonRequired, (await claims.ReturnAsync(id, "  ")).Outcome);
    }

    // A submitted claim is in somebody's queue and a paid one is in the books.
    // Neither is the filer's to erase.
    [SkippableFact]
    public async Task OnlyADraftCanBeDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IExpenseClaimService claims = Resolve<IExpenseClaimService>(provider);

        Guid draft = await FileAsync(provider, Claim());
        Assert.Equal(ExpenseOutcome.Succeeded, await claims.DeleteAsync(draft));

        Guid submitted = await FileAsync(provider, Claim());
        await claims.SubmitAsync(submitted);

        Assert.Equal(ExpenseOutcome.NotAllowed, await claims.DeleteAsync(submitted));
    }

    // Paying is a different act from agreeing to pay, often days later. An
    // unapproved claim cannot be marked paid.
    [SkippableFact]
    public async Task OnlyAnApprovedClaimCanBeReimbursed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IExpenseClaimService claims = Resolve<IExpenseClaimService>(provider);
        Guid id = await FileAsync(provider, Claim());

        Assert.Equal(ExpenseOutcome.NotAllowed, (await claims.ReimburseAsync(id)).Outcome);

        await claims.SubmitAsync(id);

        Assert.Equal(ExpenseOutcome.NotAllowed, (await claims.ReimburseAsync(id)).Outcome);
    }

    // The door that must stay shut: a valid id is not permission to act on a
    // colleague's claim through the self-service routes.
    [SkippableFact]
    public async Task SomebodyCannotTouchAnotherPersonsClaim()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IExpenseClaimService claims = Resolve<IExpenseClaimService>(provider);
        Guid mine = await FileAsync(provider, Claim());

        // Act as the manager instead.
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            (await GiveAccountAsync(_managerId, "cara2")).ToString();

        IExpenseClaimService asManager = Resolve<IExpenseClaimService>(provider);

        Assert.Equal(ExpenseOutcome.NotFound, (await asManager.SubmitAsync(mine)).Outcome);
        Assert.Equal(ExpenseOutcome.NotFound, (await asManager.UpdateAsync(mine, Claim())).Outcome);
        Assert.Equal(ExpenseOutcome.NotFound, await asManager.DeleteAsync(mine));
    }

    // Self-service reads only the caller's claims however the query is dressed up.
    [SkippableFact]
    public async Task SelfServiceReadsOnlyTheCallersOwnClaims()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await FileAsync(provider, Claim());

        PagedResult<ExpenseClaimDto> mine = await Resolve<IExpenseClaimService>(provider)
            .MineAsync(new ExpenseQuery { EmployeeId = _managerId });

        Assert.Equal(_employeeId, Assert.Single(mine.Items).EmployeeId);
    }
}
