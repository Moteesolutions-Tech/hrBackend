using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Files;
using Motee.Application.Onboarding;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Files;
using Motee.Domain.Onboarding;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// The compliance half of pre-boarding. Most of what matters here is country-specific:
// Nigeria asks for guarantors and their IDs, the UK for a tax declaration, and neither
// should ever be asked of the other.
[Collection(PostgresCollection.Name)]
public class JoinerPackTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _recordId;

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

        Guid employeeId = (await Resolve<IEmployeeService>(provider).CreateAsync(
            new EmployeeRequest
            {
                FirstName = "Ben",
                LastName = "Adeyemi",
                Email = "ben@acme.com",
                Phone = "08012345678",
                JobTitle = "Engineer",
                DepartmentId = departmentId,
                EmploymentType = EmploymentType.FullTime,
                StartDate = new DateOnly(2026, 7, 1),
            })).Employee!.Id;

        _recordId = (await Resolve<IOnboardingService>(provider)
            .EnsureAsync(employeeId)).Id;

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IJoinerPackService Pack(ServiceProvider provider) =>
        Resolve<IJoinerPackService>(provider);

    private static async Task<Guid> UploadAsync(ServiceProvider provider, string name = "passport.pdf")
    {
        byte[] bytes = Encoding.UTF8.GetBytes("%PDF-1.4 scan");

        FileUploadResult result = await Resolve<IFileUploadService>(provider).UploadAsync(
            new FileUpload
            {
                Purpose = FilePurpose.EmployeeDocument,
                FileName = name,
                ContentType = "application/pdf",
                SizeBytes = bytes.Length,
                Content = new MemoryStream(bytes),
            });

        Assert.True(result.Succeeded, result.Rejection.ToString());

        return result.File!.Id;
    }

    private static GuarantorRequest Guarantor(int position) => new()
    {
        Position = position,
        Name = $"Guarantor {position}",
        Relationship = "Uncle",
        Occupation = "Teacher",
        Address = "12 Allen Avenue, Ikeja",
        Phone = "08098765432",
    };

    // Nothing may be collected before the notice is accepted. That is the whole point of
    // a gate: consent given at the end would not be consent to collect what came before.
    [SkippableFact]
    public async Task NothingCanBeCollectedBeforeTheNoticeIsAccepted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            JoinerPackOutcome.ConsentMissing,
            (await Pack(provider).AttachDocumentAsync(
                _recordId, JoinerDocumentKind.Passport, await UploadAsync(provider))).Outcome);

        Assert.Equal(
            JoinerPackOutcome.ConsentMissing,
            (await Pack(provider).SaveGuarantorsAsync(
                _recordId, [Guarantor(1), Guarantor(2)])).Outcome);
    }

    // The version is stamped by the backend, not taken from the caller — a client that
    // chose it could record agreement to a notice that never existed.
    [SkippableFact]
    public async Task ConsentRecordsTheNoticeVersionInForce()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        JoinerPackResult accepted = await Pack(provider).AcceptPrivacyNoticeAsync(_recordId);

        Assert.True(accepted.Succeeded, accepted.Outcome.ToString());
        Assert.Equal(PrivacyNotice.CurrentVersion, accepted.Pack!.PrivacyConsent!.NoticeVersion);
    }

    // A joiner who reloads the page has not consented twice. The first acceptance is the
    // one that counts.
    [SkippableFact]
    public async Task AcceptingTwiceKeepsTheFirstTimestamp()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateTimeOffset first = (await Pack(provider).AcceptPrivacyNoticeAsync(_recordId))
            .Pack!.PrivacyConsent!.AcceptedAt;

        DateTimeOffset second = (await Pack(provider).AcceptPrivacyNoticeAsync(_recordId))
            .Pack!.PrivacyConsent!.AcceptedAt;

        Assert.Equal(first, second);
    }

    // Replaced, not added alongside — nobody should have to work out which of two
    // passports is the live one.
    [SkippableFact]
    public async Task ReuploadingASlotReplacesIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await Pack(provider).AcceptPrivacyNoticeAsync(_recordId);

        await Pack(provider).AttachDocumentAsync(
            _recordId, JoinerDocumentKind.Passport, await UploadAsync(provider, "old.pdf"));

        Guid replacement = await UploadAsync(provider, "new.pdf");

        JoinerPackResult result = await Pack(provider).AttachDocumentAsync(
            _recordId, JoinerDocumentKind.Passport, replacement);

        JoinerDocumentDto document = Assert.Single(result.Pack!.Documents);

        Assert.Equal(replacement, document.FileId);
        Assert.Equal("new.pdf", document.FileName);
    }

    // A slot offered to one country must not be accepted from the other, or a UK joiner
    // files a guarantor ID nobody will ever look at.
    [SkippableFact]
    public async Task AUkJoinerCannotFileAGuarantorId()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync("GB");
        await Pack(provider).AcceptPrivacyNoticeAsync(_recordId);

        Assert.Equal(
            JoinerPackOutcome.NotApplicable,
            (await Pack(provider).AttachDocumentAsync(
                _recordId, JoinerDocumentKind.Guarantor1Id, await UploadAsync(provider))).Outcome);
    }

    [SkippableFact]
    public async Task ANigerianJoinerCannotFileAVisa()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await Pack(provider).AcceptPrivacyNoticeAsync(_recordId);

        Assert.Equal(
            JoinerPackOutcome.NotApplicable,
            (await Pack(provider).AttachDocumentAsync(
                _recordId, JoinerDocumentKind.Visa, await UploadAsync(provider))).Outcome);
    }

    // The client asked that passport, right to work and proof of address not be
    // compulsory. Only the Nigerian guarantor IDs are required.
    [SkippableFact]
    public async Task OnlyTheGuarantorIdsAreRequired()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        JoinerPackRequirementsDto requirements = await Pack(provider).RequirementsAsync();

        Assert.Equal(
            [JoinerDocumentKind.Guarantor1Id, JoinerDocumentKind.Guarantor2Id],
            requirements.Documents.Where(spec => spec.Required).Select(spec => spec.Kind));

        Assert.Equal(2, requirements.GuarantorsRequired);
        Assert.False(requirements.CollectsStarterTax);
    }

    [SkippableFact]
    public async Task AUkTenantCollectsTaxAndNoGuarantors()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync("GB");

        JoinerPackRequirementsDto requirements = await Pack(provider).RequirementsAsync();

        Assert.DoesNotContain(requirements.Documents, spec => spec.Required);
        Assert.Equal(0, requirements.GuarantorsRequired);
        Assert.True(requirements.CollectsStarterTax);
    }

    [SkippableFact]
    public async Task GuarantorsAreSavedAsAPair()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await Pack(provider).AcceptPrivacyNoticeAsync(_recordId);

        JoinerPackResult saved = await Pack(provider)
            .SaveGuarantorsAsync(_recordId, [Guarantor(1), Guarantor(2)]);

        Assert.True(saved.Succeeded, saved.Outcome.ToString());
        Assert.Equal([1, 2], saved.Pack!.Guarantors.Select(g => g.Position));
    }

    // One guarantor is not a pair. Saving partially would leave a joiner able to submit
    // with only the first.
    [SkippableFact]
    public async Task OneGuarantorIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await Pack(provider).AcceptPrivacyNoticeAsync(_recordId);

        Assert.Equal(
            JoinerPackOutcome.InvalidGuarantors,
            (await Pack(provider).SaveGuarantorsAsync(_recordId, [Guarantor(1)])).Outcome);

        // And two at the same position is not two guarantors.
        Assert.Equal(
            JoinerPackOutcome.InvalidGuarantors,
            (await Pack(provider).SaveGuarantorsAsync(
                _recordId, [Guarantor(1), Guarantor(1)])).Outcome);
    }

    [SkippableFact]
    public async Task AUkTenantIsNotAskedForGuarantors()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync("GB");
        await Pack(provider).AcceptPrivacyNoticeAsync(_recordId);

        Assert.Equal(
            JoinerPackOutcome.NotApplicable,
            (await Pack(provider).SaveGuarantorsAsync(
                _recordId, [Guarantor(1), Guarantor(2)])).Outcome);
    }

    // The declaration is resolved from the answers, never taken from the caller — and the
    // code is derived from that.
    [SkippableFact]
    public async Task TheTaxCodeIsDerivedFromTheAnswers()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync("GB");
        await Pack(provider).AcceptPrivacyNoticeAsync(_recordId);

        JoinerPackResult saved = await Pack(provider).SaveStarterTaxAsync(
            _recordId,
            new StarterTaxRequest
            {
                Source = StarterTaxSource.StarterChecklist,
                EmployeeStatement = new EmployeeStatementAnswers { HasAnotherJob = true },
            });

        Assert.True(saved.Succeeded, saved.Outcome.ToString());

        StarterTaxDto tax = saved.Pack!.StarterTax!;

        // Another job, so statement C and basic rate.
        Assert.Equal(StarterDeclaration.C, tax.StarterChecklist!.StarterDeclaration);
        Assert.Equal("BR", tax.Derived!.TaxCode);
        Assert.Null(tax.Derived.Basis);

        // Anchored to the employee's start date, and retained for that tax year plus three.
        Assert.Equal(new DateOnly(2026, 7, 1), tax.EmploymentStartDate);
        Assert.Equal(new DateOnly(2030, 4, 5), tax.RetainUntil);
    }

    [SkippableFact]
    public async Task ASourceWithoutItsFormIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync("GB");
        await Pack(provider).AcceptPrivacyNoticeAsync(_recordId);

        Assert.Equal(
            JoinerPackOutcome.ContradictorySource,
            (await Pack(provider).SaveStarterTaxAsync(
                _recordId,
                new StarterTaxRequest { Source = StarterTaxSource.P45 })).Outcome);
    }

    // Only one branch is stored, whatever was sent. "Both" is not a state a joiner can
    // be in.
    [SkippableFact]
    public async Task OnlyTheDeclaredBranchIsKept()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync("GB");
        await Pack(provider).AcceptPrivacyNoticeAsync(_recordId);

        JoinerPackResult saved = await Pack(provider).SaveStarterTaxAsync(
            _recordId,
            new StarterTaxRequest
            {
                Source = StarterTaxSource.StarterChecklist,
                EmployeeStatement = new EmployeeStatementAnswers(),
                P45 = new P45Details
                {
                    LeavingDate = new DateOnly(2026, 6, 1),
                    TaxCodeAtLeaving = "1185L",
                },
            });

        Assert.Null(saved.Pack!.StarterTax!.P45);
        Assert.NotNull(saved.Pack.StarterTax.StarterChecklist);
    }

    [SkippableFact]
    public async Task ANigerianTenantIsNotAskedForUkTax()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await Pack(provider).AcceptPrivacyNoticeAsync(_recordId);

        Assert.Equal(
            JoinerPackOutcome.NotApplicable,
            (await Pack(provider).SaveStarterTaxAsync(
                _recordId,
                new StarterTaxRequest { Source = StarterTaxSource.None })).Outcome);
    }

    // A half-filled draft is accepted without validation — one that had to be valid would
    // not be a draft. What comes back is equivalent, not byte-identical: it is stored as
    // jsonb, which reorders keys and normalises whitespace.
    [SkippableFact]
    public async Task AHalfFilledDraftIsKeptAndMarksThemAsStarted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        const string half = """{"firstName":"Ben","bank":{"accountNumber":null}}""";

        Assert.Equal(
            JoinerPackOutcome.Succeeded,
            await Pack(provider).SaveDraftAsync(_recordId, half, step: 3));

        JoinerPackDto pack = (await Pack(provider).GetAsync(_recordId))!;

        using JsonDocument stored = JsonDocument.Parse(pack.DraftJson!);

        // A null left mid-edit survives, which is the case a validating store would reject.
        Assert.Equal("Ben", stored.RootElement.GetProperty("firstName").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            stored.RootElement.GetProperty("bank").GetProperty("accountNumber").ValueKind);

        Assert.Equal(3, pack.DraftStep);
        Assert.NotNull(pack.DraftSavedAt);

        // And the pipeline now shows them as started rather than as somebody who has not
        // opened the link, which is the point of the tracking panel.
        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        OnboardingRecord record = await context.OnboardingRecords
            .FirstAsync(candidate => candidate.Id == _recordId);

        Assert.Equal(OnboardingSubmission.InProgress, record.Submission);
    }

    // The refusal names what is missing. "Incomplete" alone sends somebody hunting
    // through eight steps.
    [SkippableFact]
    public async Task DeclaringWithOutstandingItemsNamesThem()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await Pack(provider).AcceptPrivacyNoticeAsync(_recordId);

        JoinerPackResult declared = await Pack(provider).DeclareAsync(_recordId, "Ben Adeyemi");

        Assert.Equal(JoinerPackOutcome.Incomplete, declared.Outcome);
        Assert.Contains("Guarantor 1 — ID Document", declared.Outstanding);
        Assert.Contains(declared.Outstanding, item => item.StartsWith("Guarantor details", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task ACompletePackCanBeDeclared()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await Pack(provider).AcceptPrivacyNoticeAsync(_recordId);

        await Pack(provider).SaveGuarantorsAsync(_recordId, [Guarantor(1), Guarantor(2)]);

        await Pack(provider).AttachDocumentAsync(
            _recordId, JoinerDocumentKind.Guarantor1Id, await UploadAsync(provider, "g1.pdf"));

        await Pack(provider).AttachDocumentAsync(
            _recordId, JoinerDocumentKind.Guarantor2Id, await UploadAsync(provider, "g2.pdf"));

        JoinerPackResult declared = await Pack(provider).DeclareAsync(_recordId, "  Ben Adeyemi  ");

        Assert.True(declared.Succeeded, string.Join("; ", declared.Outstanding));
        Assert.Equal("Ben Adeyemi", declared.Pack!.Declaration!.SignedName);
        Assert.True(declared.Pack.IsComplete);
    }

    // A UK joiner must be asked about tax, even to say "neither form" — the emergency
    // code is recoverable, never being asked is not.
    [SkippableFact]
    public async Task AUkPackIsIncompleteUntilTaxIsDeclared()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync("GB");
        await Pack(provider).AcceptPrivacyNoticeAsync(_recordId);

        JoinerPackResult first = await Pack(provider).DeclareAsync(_recordId, "Ben Adeyemi");

        Assert.Equal(JoinerPackOutcome.Incomplete, first.Outcome);
        Assert.Contains("Tax details (P45 or Starter Checklist)", first.Outstanding);

        // Declaring neither form is a valid answer, and completes the step.
        await Pack(provider).SaveStarterTaxAsync(
            _recordId, new StarterTaxRequest { Source = StarterTaxSource.None });

        JoinerPackResult second = await Pack(provider).DeclareAsync(_recordId, "Ben Adeyemi");

        Assert.True(second.Succeeded, string.Join("; ", second.Outstanding));
        Assert.Equal("0T", second.Pack!.StarterTax!.Derived!.TaxCode);
    }

    // A file uploaded for something else is not a document somebody chose to attach here.
    [SkippableFact]
    public async Task AFileFromAnotherPurposeIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await Pack(provider).AcceptPrivacyNoticeAsync(_recordId);

        // Real PNG magic bytes: the upload sniffs content, so "avatar" as text would be
        // rejected for not matching its declared type before reaching the check below.
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

        FileUploadResult avatar = await Resolve<IFileUploadService>(provider).UploadAsync(
            new FileUpload
            {
                Purpose = FilePurpose.EmployeeAvatar,
                FileName = "me.png",
                ContentType = "image/png",
                SizeBytes = bytes.Length,
                Content = new MemoryStream(bytes),
            });

        Assert.True(avatar.Succeeded, avatar.Rejection.ToString());

        Assert.Equal(
            JoinerPackOutcome.UnknownFile,
            (await Pack(provider).AttachDocumentAsync(
                _recordId, JoinerDocumentKind.Passport, avatar.File!.Id)).Outcome);
    }
}
