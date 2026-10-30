using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Contracts;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Contracts;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// Contracts, issued and signed.
//
// Written before the service. The rule carrying most of the weight is the last one
// here: once somebody has signed, the terms are fixed. A signature is against what
// the contract said at the moment it was given, and editing it afterwards is
// forgery with extra steps — so the service has to refuse rather than trust that no
// screen offers the button.
[Collection(PostgresCollection.Name)]
public class ContractServiceTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _employeeId;
    private Guid _colleagueId;

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

        _employeeId = await HireAsync(provider, departmentId, "Bola", "bola@acme.com");
        _colleagueId = await HireAsync(provider, departmentId, "Cara", "cara@acme.com");

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
            FirstName = char.ToUpperInvariant(handle[0]) + handle[1..],
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
        string email) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = email,
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
        })).Employee!.Id;

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    // The signed copy, already uploaded. Seeded directly rather than through the
    // upload endpoint, which has its own tests and would put S3 in the middle.
    private async Task<Guid> PdfAsync()
    {
        Guid id = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.StoredFiles.Add(new Motee.Domain.Files.StoredFile
        {
            Id = id,
            TenantId = _tenantId,
            Purpose = Motee.Domain.Files.FilePurpose.CompanyDocument,
            FileName = "contract-signed.pdf",
            ContentType = "application/pdf",
            SizeBytes = 2048,
            StorageKey = $"documents/{id}",
            UploadedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();

        return id;
    }

    private ContractRequest Request(
        DateOnly? endDate = null,
        bool autoRenew = false,
        int noticeDays = 30,
        decimal? salary = 5_000_000m) => new()
    {
        EmployeeId = _employeeId,
        Title = "Employment Contract",
        Description = "Full-time, permanent.",
        ContractType = ContractType.Employment,
        StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-6),
        EndDate = endDate,
        AutoRenew = autoRenew,
        NoticePeriodDays = noticeDays,
        Salary = salary,
    };

    private static async Task<Guid> IssueAsync(ServiceProvider provider, ContractRequest request)
    {
        ContractResult created = await Resolve<IContractService>(provider).CreateAsync(request);

        Assert.True(created.Succeeded, $"{created.Outcome}: {created.Reason}");

        return created.Contract!.Id;
    }

    // Sent out and put in force. Expiry only means anything for a contract that is
    // actually running — a draft has no end to approach — so the tests about it
    // have to get one there first.
    private static async Task<ContractDto> InForceAsync(
        ServiceProvider provider,
        ContractRequest request)
    {
        IContractService contracts = Resolve<IContractService>(provider);

        Guid id = await IssueAsync(provider, request);

        await contracts.SendForSignatureAsync(id);

        return (await contracts.ActivateAsync(id)).Contract!;
    }

    [SkippableFact]
    public async Task ANewContractIsADraftNobodyHasSigned()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ContractDto contract = (await Resolve<IContractService>(provider)
            .CreateAsync(Request())).Contract!;

        Assert.Equal(ContractStatus.Draft, contract.Status);
        Assert.Equal(SignatureStatus.Unsigned, contract.SignatureStatus);
        Assert.Empty(contract.Signatures);

        // The company's own, like an expense claim.
        Assert.Equal("NGN", contract.Currency);
    }

    [SkippableFact]
    public async Task AContractEndingBeforeItStartsIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            ContractOutcome.EndBeforeStart,
            (await Resolve<IContractService>(provider).CreateAsync(
                Request(endDate: DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-2))))
                .Outcome);
    }

    [SkippableFact]
    public async Task ANegativeSalaryIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            ContractOutcome.AmountNotPositive,
            (await Resolve<IContractService>(provider).CreateAsync(Request(salary: -1m))).Outcome);
    }

    // Draft → pending → active, and terminate from either of the last two.
    [SkippableFact]
    public async Task AContractMovesThroughItsLifecycle()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IContractService contracts = Resolve<IContractService>(provider);
        Guid id = await IssueAsync(provider, Request());

        Assert.Equal(
            ContractStatus.PendingSignature,
            (await contracts.SendForSignatureAsync(id)).Contract!.Status);

        Assert.Equal(ContractStatus.Active, (await contracts.ActivateAsync(id)).Contract!.Status);

        Assert.Equal(
            ContractStatus.Terminated,
            (await contracts.TerminateAsync(id, "Resigned.")).Contract!.Status);
    }

    [SkippableFact]
    public async Task ADraftCannotBeActivatedWithoutBeingSentOut()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IContractService contracts = Resolve<IContractService>(provider);
        Guid id = await IssueAsync(provider, Request());

        ContractResult refused = await contracts.ActivateAsync(id);

        Assert.Equal(ContractOutcome.NotAllowed, refused.Outcome);
        Assert.Contains("before putting it in force", refused.Reason!, StringComparison.Ordinal);
    }

    // Reopening would leave the termination describing something that no longer
    // happened. A contract that comes back is a new one.
    [SkippableFact]
    public async Task ATerminatedContractDoesNotReopen()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IContractService contracts = Resolve<IContractService>(provider);
        Guid id = await IssueAsync(provider, Request());

        await contracts.SendForSignatureAsync(id);
        await contracts.TerminateAsync(id, "Mutual agreement.");

        Assert.Equal(ContractOutcome.NotAllowed, (await contracts.ActivateAsync(id)).Outcome);
        Assert.Equal(
            ContractOutcome.NotAllowed, (await contracts.SendForSignatureAsync(id)).Outcome);
    }

    [SkippableFact]
    public async Task TerminatingNeedsAReason()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IContractService contracts = Resolve<IContractService>(provider);
        Guid id = await IssueAsync(provider, Request());

        await contracts.SendForSignatureAsync(id);

        Assert.Equal(
            ContractOutcome.ReasonRequired, (await contracts.TerminateAsync(id, "  ")).Outcome);
    }

    // ── signing ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneSignatureIsPartialAndTwoIsFull()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IContractService contracts = Resolve<IContractService>(provider);
        Guid id = await IssueAsync(provider, Request());

        await contracts.SendForSignatureAsync(id);

        ContractDto afterEmployee = (await contracts.SignMineAsync(
            id, new SignContractRequest { SignedAs = "Employee" })).Contract!;

        Assert.Equal(SignatureStatus.PartiallySigned, afterEmployee.SignatureStatus);

        ContractDto afterCompany = (await contracts.SignAsync(
            id, new SignContractRequest { SignedAs = "HR Director" })).Contract!;

        Assert.Equal(SignatureStatus.FullySigned, afterCompany.SignatureStatus);
        Assert.Equal(2, afterCompany.Signatures.Count);
    }

    // Twice would move the date and make a late signature look prompt.
    [SkippableFact]
    public async Task NobodySignsTheSameContractTwice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IContractService contracts = Resolve<IContractService>(provider);
        Guid id = await IssueAsync(provider, Request());

        await contracts.SendForSignatureAsync(id);
        await contracts.SignMineAsync(id, new SignContractRequest { SignedAs = "Employee" });

        Assert.Equal(
            ContractOutcome.AlreadySigned,
            (await contracts.SignMineAsync(
                id, new SignContractRequest { SignedAs = "Employee" })).Outcome);
    }

    // The rule that matters most. A signature is against what the contract said at
    // the moment it was given.
    [SkippableFact]
    public async Task TheTermsAreFixedOnceSomebodyHasSigned()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IContractService contracts = Resolve<IContractService>(provider);
        Guid id = await IssueAsync(provider, Request());

        await contracts.SendForSignatureAsync(id);
        await contracts.SignMineAsync(id, new SignContractRequest { SignedAs = "Employee" });

        ContractResult refused = await contracts.UpdateAsync(id, Request(salary: 1m));

        Assert.Equal(ContractOutcome.SignedContractIsFixed, refused.Outcome);
    }

    // A valid id is not permission to sign somebody else's contract as them.
    [SkippableFact]
    public async Task SomebodyCannotSignAnotherPersonsContract()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IContractService contracts = Resolve<IContractService>(provider);

        Guid theirs = await IssueAsync(
            provider, Request() with { EmployeeId = _colleagueId });

        await contracts.SendForSignatureAsync(theirs);

        Assert.Equal(
            ContractOutcome.NotFound,
            (await contracts.SignMineAsync(
                theirs, new SignContractRequest { SignedAs = "Employee" })).Outcome);
    }

    [SkippableFact]
    public async Task SomebodySeesOnlyTheirOwnContracts()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IContractService contracts = Resolve<IContractService>(provider);

        await IssueAsync(provider, Request());
        await IssueAsync(provider, Request() with { EmployeeId = _colleagueId });

        Assert.Equal(_employeeId, Assert.Single(await contracts.MineAsync()).EmployeeId);
    }

    // ── expiry, derived ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AContractPastItsEndDateReadsAsExpired()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ContractDto contract = (await Resolve<IContractService>(provider).CreateAsync(
            Request(endDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1)))).Contract!;

        Assert.True(contract.IsExpired);
        Assert.False(contract.IsExpiringSoon);
    }

    // The window comes from the notice period, because that is what the number is
    // for: three months' notice needs flagging three months out.
    [SkippableFact]
    public async Task TheExpiringWindowFollowsTheNoticePeriod()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly inSixtyDays = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(60);

        // Thirty days' notice: sixty days out is not yet soon.
        Assert.False(
            (await InForceAsync(provider, Request(endDate: inSixtyDays, noticeDays: 30)))
                .IsExpiringSoon);

        // Ninety days' notice on the same date: already inside the window.
        Assert.True(
            (await InForceAsync(provider, Request(endDate: inSixtyDays, noticeDays: 90)))
                .IsExpiringSoon);
    }

    // Auto-renewing contracts are continuing, not expiring. Flagging them would
    // bury the ones somebody has to act on.
    [SkippableFact]
    public async Task AnAutoRenewingContractIsNeverExpiringSoon()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ContractDto renewing = await InForceAsync(
            provider,
            Request(endDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5), autoRenew: true));

        Assert.False(renewing.IsExpiringSoon);
    }

    // An open-ended contract has no end date, and a far-future one standing in for
    // "never" would make every report guess which dates were real.
    [SkippableFact]
    public async Task AnOpenEndedContractNeverExpires()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ContractDto open = (await Resolve<IContractService>(provider)
            .CreateAsync(Request())).Contract!;

        Assert.Null(open.EndDate);
        Assert.False(open.IsExpired);
        Assert.False(open.IsExpiringSoon);
    }

    [SkippableFact]
    public async Task TheExpiringFilterFindsOnlyTheOnesNeedingAction()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IContractService contracts = Resolve<IContractService>(provider);

        DateOnly soon = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);

        await InForceAsync(provider, Request());
        await InForceAsync(provider, Request(endDate: soon, autoRenew: true));
        Guid acting = (await InForceAsync(provider, Request(endDate: soon))).Id;

        PagedResult<ContractDto> expiring =
            await contracts.ListAsync(new ContractQuery { ExpiringSoonOnly = true });

        Assert.Equal(acting, Assert.Single(expiring.Items).Id);
    }

    // ── notes ───────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ANoteIsKeptAgainstTheContract()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IContractService contracts = Resolve<IContractService>(provider);
        Guid id = await IssueAsync(provider, Request());

        ContractDto after = (await contracts.AddNoteAsync(id, "Salary agreed verbally."))
            .Contract!;

        ContractNoteDto note = Assert.Single(after.Notes);

        Assert.Equal("Salary agreed verbally.", note.Content);
        Assert.Equal("Bola Okafor", note.CreatedByName);
    }

    [SkippableFact]
    public async Task AnEmptyNoteIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IContractService contracts = Resolve<IContractService>(provider);
        Guid id = await IssueAsync(provider, Request());

        Assert.Equal(
            ContractOutcome.ContentRequired, (await contracts.AddNoteAsync(id, " ")).Outcome);
    }

    // ── filing into documents ───────────────────────────────────────────────

    // The two modules hold different things — the terms and their lifecycle on
    // one side, the artefact somebody opens on the other — and this is the link.
    // An employee should find their contract where they look for every other
    // document, without the contracts screen reimplementing sharing.
    [SkippableFact]
    public async Task FilingAContractPutsItInDocumentsAndSharesItWithThem()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IContractService contracts = Resolve<IContractService>(provider);
        Guid id = await IssueAsync(provider, Request());

        await contracts.SendForSignatureAsync(id);

        ContractResult filed = await contracts.FileToDocumentsAsync(id, await PdfAsync());

        Assert.True(filed.Succeeded, $"{filed.Outcome}: {filed.Reason}");
        Assert.NotNull(filed.Contract!.DocumentId);

        // And it is theirs to find, with permission to keep a copy — it is their
        // own contract.
        Application.Documents.DocumentDto mine = Assert.Single(
            await Resolve<Application.Documents.IDocumentService>(provider).MineAsync());

        Assert.Equal(filed.Contract.DocumentId, mine.Id);
        Assert.Equal(Domain.Documents.DocumentCategory.Contract, mine.Category);
        Assert.Equal(
            Domain.Documents.DocumentPermission.Download,
            Assert.Single(mine.Shares).Permission);
    }

    // Filing twice would leave them with two copies and nothing to say which is
    // current.
    [SkippableFact]
    public async Task AContractIsNotFiledTwice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IContractService contracts = Resolve<IContractService>(provider);
        Guid id = await IssueAsync(provider, Request());

        await contracts.SendForSignatureAsync(id);
        await contracts.FileToDocumentsAsync(id, await PdfAsync());

        Assert.Equal(
            ContractOutcome.AlreadyFiled,
            (await contracts.FileToDocumentsAsync(id, await PdfAsync())).Outcome);
    }

    // There is no agreed copy of a draft, so filing one files nothing.
    [SkippableFact]
    public async Task ADraftCannotBeFiled()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IContractService contracts = Resolve<IContractService>(provider);

        Assert.Equal(
            ContractOutcome.NotReadyToFile,
            (await contracts.FileToDocumentsAsync(
                await IssueAsync(provider, Request()), await PdfAsync())).Outcome);
    }

    [SkippableFact]
    public async Task FilingWithoutAnUploadedCopyIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IContractService contracts = Resolve<IContractService>(provider);
        Guid id = await IssueAsync(provider, Request());

        await contracts.SendForSignatureAsync(id);

        Assert.Equal(
            ContractOutcome.FileNotFound,
            (await contracts.FileToDocumentsAsync(id, Guid.NewGuid())).Outcome);
    }

    // A draft nobody has seen is deletable; a signed contract is a record.
    [SkippableFact]
    public async Task OnlyAnUnsignedDraftCanBeDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IContractService contracts = Resolve<IContractService>(provider);

        Guid draft = await IssueAsync(provider, Request());
        Assert.Equal(ContractOutcome.Succeeded, await contracts.DeleteAsync(draft));

        Guid signed = await IssueAsync(provider, Request());
        await contracts.SendForSignatureAsync(signed);
        await contracts.SignMineAsync(signed, new SignContractRequest { SignedAs = "Employee" });

        Assert.Equal(ContractOutcome.NotAllowed, await contracts.DeleteAsync(signed));
    }
}
