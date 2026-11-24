using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Announcements;
using Motee.Application.Auth;
using Motee.Application.Company;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Announcements;
using Motee.Domain.Common;
using Motee.Domain.Company;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// The company's own record, out of the database.
//
// Three things these are really about. The profile reads from whatever already
// owns each part — the address from the branch marked Headquarters, the
// headcount from the employees, the pinned notice from the announcements
// module. The org chart returns every top-of-chain person rather than one. And
// verification is split: the company submits, Motee staff review, because a
// tenant marking its own registration verified is a label rather than a check.
//
// Written before the services.
[Collection(PostgresCollection.Name)]
public class CompanyServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero));

    private static readonly DateOnly Today = new(2026, 6, 15);

    private Guid _tenantId;
    private Guid _ownerUserId;
    private Guid _departmentId;

    // ── the profile ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheProfileComesBackFromTheTenant()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        CompanyProfileDto profile = await Company(provider).ProfileAsync();

        Assert.Equal("Acme Corporation", profile.Name);
        Assert.Equal(CountryCode.Nigeria, profile.CountryCode);
        Assert.Equal("Nigeria", profile.CountryName);
    }

    [SkippableFact]
    public async Task TheProfileIsSaved()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        CompanyResult<CompanyProfileDto> saved = await Company(provider).SaveProfileAsync(
            new CompanyProfileRequest
            {
                Name = "Acme",
                LegalName = "Acme Technologies Limited",
                Industry = "Technology",
                ContactEmail = "hello@acme.com",
                ContactPhone = "08012345678",
                Website = "https://acme.test",
            });

        Assert.True(saved.Succeeded, $"{saved.Outcome} / {saved.Refusal}");

        CompanyProfileDto profile = await Company(provider).ProfileAsync();

        Assert.Equal("Acme", profile.Name);
        Assert.Equal("Acme Technologies Limited", profile.LegalName);
        Assert.Equal("hello@acme.com", profile.ContactEmail);
        Assert.Equal("https://acme.test", profile.Website);
    }

    // The address comes from the site everybody works at, not a box typed
    // beside it — branches already hold address lines, city, region and
    // country.
    [SkippableFact]
    public async Task TheAddressComesFromTheHeadOffice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid branchId = await HeadOfficeAsync(provider);

        CompanyProfileDto profile = await Company(provider).ProfileAsync();

        Assert.Equal(branchId, profile.HeadOfficeBranchId);
        Assert.Equal("Lagos HQ", profile.HeadOfficeName);
        Assert.NotNull(profile.Address);
        Assert.Contains("12 Marina Road", profile.Address);
        Assert.Contains("Lagos", profile.Address);
    }

    // Saying so beats inventing one.
    [SkippableFact]
    public async Task WithNoHeadOfficeThereIsNoAddress()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        CompanyProfileDto profile = await Company(provider).ProfileAsync();

        Assert.Null(profile.Address);
        Assert.Null(profile.HeadOfficeBranchId);
    }

    // A branch that is not the head office is not the company's address.
    [SkippableFact]
    public async Task AnOrdinaryBranchIsNotTheCompanyAddress()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Resolve<IBranchService>(provider).CreateAsync(new BranchRequest
        {
            Name = "Abuja",
            Code = "ABJ",
            Kind = BranchKind.Branch,
            Status = BranchStatus.Active,
            AddressLines = ["1 Garki Street"],
            City = "Abuja",
            Country = "Nigeria",
        });

        Assert.Null((await Company(provider).ProfileAsync()).Address);
    }

    // The screen derives its size band from the employee count while the
    // wizard stores a self-reported one. Both are sent, labelled, rather than
    // one quietly winning.
    [SkippableFact]
    public async Task TheHeadcountIsCountedAndTheBandIsWhatTheyToldUs()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada");
        await HireAsync(provider, "Bolu");

        CompanyProfileDto profile = await Company(provider).ProfileAsync();

        Assert.Equal(2, profile.Headcount);
    }

    // A leaver is not on the books.
    [SkippableFact]
    public async Task AnEmployeeWhoHasLeftIsNotInTheHeadcount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada");
        Guid leaverId = await HireAsync(provider, "Bolu");

        await using (MoteeDbContext context = fixture.CreateContext(_tenantId))
        {
            await context.Employees
                .Where(employee => employee.Id == leaverId)
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(employee => employee.Status, EmployeeStatus.Inactive));
        }

        Assert.Equal(1, (await Company(provider).ProfileAsync()).Headcount);
    }

    [SkippableFact]
    public async Task ACompanyNeedsAName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.False(
            (await Company(provider).SaveProfileAsync(
                new CompanyProfileRequest { Name = "  " })).Succeeded);
    }

    // ── the org chart ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheOrgChartIsBuiltFromReportingLines()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid chief = await HireAsync(provider, "Chief");
        Guid head = await HireAsync(provider, "Head", managerId: chief);
        await HireAsync(provider, "Junior", managerId: head);

        OrgChartView chart = await Company(provider).OrgChartAsync();

        Assert.Equal(3, chart.Nodes.Count);
        Assert.Equal([chief], chart.Roots);
        Assert.Equal(3, chart.Nodes.Single(node => node.Name == "Junior Okafor").Level);
    }

    // The screen renders from a single root, so a company with two people at
    // the top loses half its chart. Both come back.
    [SkippableFact]
    public async Task TwoPeopleAtTheTopAreBothReturnedAsRoots()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid first = await HireAsync(provider, "Chief");
        Guid second = await HireAsync(provider, "Founder");

        OrgChartView chart = await Company(provider).OrgChartAsync();

        Assert.Equal(2, chart.Roots.Count);
        Assert.Contains(first, chart.Roots);
        Assert.Contains(second, chart.Roots);
    }

    // A chart carrying leavers draws reporting lines to somebody who left two
    // years ago.
    [SkippableFact]
    public async Task SomebodyWhoHasLeftIsNotOnTheChart()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid chief = await HireAsync(provider, "Chief");
        Guid leaver = await HireAsync(provider, "Gone", managerId: chief);

        await using (MoteeDbContext context = fixture.CreateContext(_tenantId))
        {
            await context.Employees
                .Where(employee => employee.Id == leaver)
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(employee => employee.Status, EmployeeStatus.Inactive));
        }

        OrgChartView chart = await Company(provider).OrgChartAsync();

        Assert.Single(chart.Nodes);
        Assert.Equal([chief], chart.Roots);
    }

    // Somebody whose manager has left is detached rather than promoted to a
    // second chief executive.
    [SkippableFact]
    public async Task SomebodyWhoseManagerHasLeftIsFlaggedAsDetached()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid chief = await HireAsync(provider, "Chief");
        Guid report = await HireAsync(provider, "Report", managerId: chief);

        await using (MoteeDbContext context = fixture.CreateContext(_tenantId))
        {
            await context.Employees
                .Where(employee => employee.Id == chief)
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(employee => employee.Status, EmployeeStatus.Inactive));
        }

        OrgChartView chart = await Company(provider).OrgChartAsync();

        OrgChartNode node = Assert.Single(chart.Nodes);

        Assert.Equal(report, node.Id);
        Assert.True(node.Detached);
    }

    [SkippableFact]
    public async Task TheOrgChartCanBeNarrowedToTheTopTiers()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid chief = await HireAsync(provider, "Chief");
        Guid head = await HireAsync(provider, "Head", managerId: chief);
        await HireAsync(provider, "Junior", managerId: head);

        Assert.Equal(2, (await Company(provider).OrgChartAsync(maxLevel: 2)).Nodes.Count);
    }

    // ── the pinned notice ──────────────────────────────────────────────────

    // Read from the announcements module, which already has pinning. A second
    // place to store one company notice would be a second notice.
    [SkippableFact]
    public async Task ThePinnedNoticeIsThePinnedAnnouncement()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await PinAsync(provider, "Review season is open");

        PinnedNoticeDto? notice = await Company(provider).PinnedNoticeAsync();

        Assert.NotNull(notice);
        Assert.Equal("Review season is open", notice.Title);
    }

    [SkippableFact]
    public async Task WithNothingPinnedThereIsNoNotice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Null(await Company(provider).PinnedNoticeAsync());
    }

    // ── verification: the company's half ───────────────────────────────────

    // Nigeria registers with the CAC and holds a TIN. A screen asking for a
    // "Company Number" is asking for something that does not exist.
    [SkippableFact]
    public async Task TheLabelsAreTheCountrys()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        IReadOnlyList<VerificationDto> all = await Verification(provider).AllAsync();

        VerificationDto registration =
            all.Single(row => row.Kind == VerificationKind.Registration);

        Assert.Equal("RC Number", registration.NumberLabel);
        Assert.Equal("Corporate Affairs Commission", registration.Authority);
    }

    [SkippableFact]
    public async Task BothRecordsComeBackEvenBeforeAnybodyTouchesThem()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        IReadOnlyList<VerificationDto> all = await Verification(provider).AllAsync();

        Assert.Equal(2, all.Count);
        Assert.All(all, row => Assert.Equal(VerificationStage.Draft, row.Stage));
        Assert.All(all, row => Assert.False(row.CanSubmit));
    }

    [SkippableFact]
    public async Task TheDetailsAreSaved()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        CompanyResult<VerificationDto> saved = await Verification(provider).SaveDetailsAsync(
            VerificationKind.Registration,
            new VerificationDetailsRequest { Number = "RC-123456" });

        Assert.True(saved.Succeeded, $"{saved.Outcome} / {saved.Refusal}");
        Assert.Equal("RC-123456", saved.Value!.Number);
    }

    [SkippableFact]
    public async Task WhatIsStillMissingIsReported()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        CompanyResult<VerificationDto> saved = await Verification(provider).SaveDetailsAsync(
            VerificationKind.Registration,
            new VerificationDetailsRequest { Number = "RC-123456" });

        Assert.Single(
            saved.Value!.Outstanding, item => item.Contains("certificate"));
    }

    // A submission with nothing attached is a reviewer being asked to verify a
    // blank form, and the screen allows exactly that.
    [SkippableFact]
    public async Task SubmittingWithoutTheCertificateIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Verification(provider).SaveDetailsAsync(
            VerificationKind.Registration,
            new VerificationDetailsRequest { Number = "RC-123456" });

        Assert.Equal(
            VerificationRefusal.DocumentRequired,
            (await Verification(provider).SubmitAsync(VerificationKind.Registration)).Refusal);
    }

    [SkippableFact]
    public async Task SubmittingWithoutTheNumberIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            VerificationRefusal.NumberRequired,
            (await Verification(provider).SubmitAsync(VerificationKind.Registration)).Refusal);
    }

    [SkippableFact]
    public async Task ACompleteSubmissionIsAccepted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        CompanyResult<VerificationDto> submitted = await ReadyAsync(provider);

        Assert.True(submitted.Succeeded, $"{submitted.Outcome} / {submitted.Refusal}");
        Assert.Equal(VerificationStage.Submitted, submitted.Value!.Stage);
        Assert.Equal(Today, submitted.Value.SubmittedOn);
        Assert.False(submitted.Value.Editable);
    }

    // Once submitted the reviewer is looking at a particular certificate.
    // Letting the number change underneath them means the thing verified is
    // not the thing on file.
    [SkippableFact]
    public async Task TheNumberCannotChangeOnceItIsSubmitted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ReadyAsync(provider);

        Assert.Equal(
            VerificationRefusal.NotEditableNow,
            (await Verification(provider).SaveDetailsAsync(
                VerificationKind.Registration,
                new VerificationDetailsRequest { Number = "RC-999" })).Refusal);
    }

    // ── verification: the reviewer's half ──────────────────────────────────

    [SkippableFact]
    public async Task ThePlatformVerifiesIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ReadyAsync(provider);

        CompanyResult<VerificationDto> reviewed = await Verification(provider).ReviewAsync(
            _tenantId,
            VerificationKind.Registration,
            new VerificationReviewRequest { Stage = VerificationStage.Verified });

        Assert.True(reviewed.Succeeded, $"{reviewed.Outcome} / {reviewed.Refusal}");
        Assert.Equal(VerificationStage.Verified, reviewed.Value!.Stage);
        Assert.Equal(Today, reviewed.Value.ReviewedOn);
        Assert.Equal("Owner Okafor", reviewed.Value.ReviewedByName);
    }

    // "Rejected" with no reason is a dead end for whoever has to fix it.
    [SkippableFact]
    public async Task ARejectionNeedsAReason()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ReadyAsync(provider);

        Assert.Equal(
            VerificationRefusal.ReasonRequired,
            (await Verification(provider).ReviewAsync(
                _tenantId,
                VerificationKind.Registration,
                new VerificationReviewRequest
                {
                    Stage = VerificationStage.Rejected,
                })).Refusal);
    }

    [SkippableFact]
    public async Task ARejectionCarriesItsReasonBack()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ReadyAsync(provider);

        CompanyResult<VerificationDto> rejected = await RejectAsync(provider);

        Assert.Equal(VerificationStage.Rejected, rejected.Value!.Stage);
        Assert.Equal("The certificate is for another company.", rejected.Value.DecisionNote);

        // Not editable where it stands. Correcting it means reopening it first,
        // which is what withdraw is for.
        Assert.False(rejected.Value.Editable);
    }

    // Without this a rejected company is stuck: the record is not editable
    // outside draft, and nothing else could put it there.
    [SkippableFact]
    public async Task ARejectedRecordCanBeReopenedToCorrect()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ReadyAsync(provider);
        await RejectAsync(provider);

        CompanyResult<VerificationDto> reopened =
            await Verification(provider).WithdrawAsync(VerificationKind.Registration);

        Assert.True(reopened.Succeeded, $"{reopened.Outcome} / {reopened.Refusal}");
        Assert.Equal(VerificationStage.Draft, reopened.Value!.Stage);
        Assert.True(reopened.Value.Editable);

        // The reason stays readable while it is being fixed.
        Assert.Equal("The certificate is for another company.", reopened.Value.DecisionNote);
    }

    // A company that spots a wrong number should not have to wait to be
    // rejected.
    [SkippableFact]
    public async Task ASubmissionNobodyHasPickedUpCanBePulledBack()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ReadyAsync(provider);

        CompanyResult<VerificationDto> pulled =
            await Verification(provider).WithdrawAsync(VerificationKind.Registration);

        Assert.True(pulled.Succeeded, $"{pulled.Outcome} / {pulled.Refusal}");
        Assert.Equal(VerificationStage.Draft, pulled.Value!.Stage);
        Assert.Null(pulled.Value.SubmittedOn);
    }

    // Pulling it back mid-check would waste the reviewer's work and let the
    // document change after it had been seen.
    [SkippableFact]
    public async Task SomethingUnderReviewCannotBePulledBack()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ReadyAsync(provider);

        await Verification(provider).ReviewAsync(
            _tenantId,
            VerificationKind.Registration,
            new VerificationReviewRequest { Stage = VerificationStage.UnderReview });

        Assert.Equal(
            VerificationRefusal.StageNotReachable,
            (await Verification(provider).WithdrawAsync(VerificationKind.Registration))
                .Refusal);
    }

    [SkippableFact]
    public async Task AVerifiedRecordCannotBeReopenedByTheCompany()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ReadyAsync(provider);

        await Verification(provider).ReviewAsync(
            _tenantId,
            VerificationKind.Registration,
            new VerificationReviewRequest { Stage = VerificationStage.Verified });

        Assert.Equal(
            VerificationRefusal.StageNotReachable,
            (await Verification(provider).WithdrawAsync(VerificationKind.Registration))
                .Refusal);
    }

    // A previous rejection's reason beside a fresh submission reads as though
    // the new one had already been turned down.
    [SkippableFact]
    public async Task ResubmittingClearsTheOldRejection()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ReadyAsync(provider);
        await RejectAsync(provider);

        // Reopen, correct it, submit again.
        await Verification(provider).WithdrawAsync(VerificationKind.Registration);

        await Verification(provider).SaveDetailsAsync(
            VerificationKind.Registration,
            new VerificationDetailsRequest
            {
                Number = "RC-654321",
                DocumentFileId = await CertificateAsync(),
            });

        CompanyResult<VerificationDto> again =
            await Verification(provider).SubmitAsync(VerificationKind.Registration);

        Assert.True(again.Succeeded, $"{again.Outcome} / {again.Refusal}");
        Assert.Null(again.Value!.DecisionNote);
        Assert.Null(again.Value.ReviewedByName);
    }

    [SkippableFact]
    public async Task ReviewingSomethingNobodyHasSubmittedIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Verification(provider).SaveDetailsAsync(
            VerificationKind.Registration,
            new VerificationDetailsRequest { Number = "RC-123456" });

        Assert.Equal(
            VerificationRefusal.StageNotReachable,
            (await Verification(provider).ReviewAsync(
                _tenantId,
                VerificationKind.Registration,
                new VerificationReviewRequest
                {
                    Stage = VerificationStage.Verified,
                })).Refusal);
    }

    // ── history ────────────────────────────────────────────────────────────

    // From the audit trail. A history table of its own would be a second copy
    // that can disagree with the auditor's.
    [SkippableFact]
    public async Task TheHistoryComesFromTheAuditTrail()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ReadyAsync(provider);

        await Verification(provider).ReviewAsync(
            _tenantId,
            VerificationKind.Registration,
            new VerificationReviewRequest { Stage = VerificationStage.Verified });

        VerificationDto registration = (await Verification(provider).AllAsync())
            .Single(row => row.Kind == VerificationKind.Registration);

        Assert.Contains(registration.History, entry => entry.Event.Contains("verified"));
        Assert.Contains(registration.History, entry => entry.Event.Contains("submitted"));
    }

    // ── the review queue ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheQueueCarriesEverythingWaitingOnUs()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ReadyAsync(provider);

        IReadOnlyList<ReviewQueueItemDto> queue = await Verification(provider).QueueAsync();

        ReviewQueueItemDto item = Assert.Single(queue);

        Assert.Equal("Acme Corporation", item.CompanyName);
        Assert.Equal("Nigeria", item.CountryName);
        Assert.Equal("Corporate Affairs Commission", item.Authority);
        Assert.Equal(0, item.DaysWaiting);
    }

    [SkippableFact]
    public async Task ADraftIsNotInTheQueue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Verification(provider).SaveDetailsAsync(
            VerificationKind.Registration,
            new VerificationDetailsRequest { Number = "RC-123456" });

        Assert.Empty(await Verification(provider).QueueAsync());
    }

    [SkippableFact]
    public async Task SomethingVerifiedLeavesTheQueue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ReadyAsync(provider);

        await Verification(provider).ReviewAsync(
            _tenantId,
            VerificationKind.Registration,
            new VerificationReviewRequest { Stage = VerificationStage.Verified });

        Assert.Empty(await Verification(provider).QueueAsync());
    }

    // The figure a reviewer sorts by.
    [SkippableFact]
    public async Task HowLongSomethingHasBeenWaitingIsCounted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ReadyAsync(provider);

        _clock.Advance(TimeSpan.FromDays(9));

        Assert.Equal(9, (await Verification(provider).QueueAsync())[0].DaysWaiting);
    }

    // ── tenancy ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanysVerificationIsNotAnothers()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ReadyAsync(provider);

        RegisterTenantResult other = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Other",
            LastName = "Owner",
            Email = "owner@other.com",
            CompanyName = "Other Limited",
            Password = "correct-horse",
            CountryCode = "GB",
        });

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = other.TenantId;

        IReadOnlyList<VerificationDto> theirs = await Verification(provider).AllAsync();

        Assert.All(theirs, row => Assert.Equal(VerificationStage.Draft, row.Stage));

        // And the labels follow their country, not ours.
        Assert.Equal(
            "Company Number",
            theirs.Single(row => row.Kind == VerificationKind.Registration).NumberLabel);
    }

    // ── arrange ────────────────────────────────────────────────────────────

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

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            _ownerUserId.ToString();

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static ICompanyService Company(ServiceProvider provider) =>
        Resolve<ICompanyService>(provider);

    private static ICompanyVerificationService Verification(ServiceProvider provider) =>
        Resolve<ICompanyVerificationService>(provider);

    private static async Task<Guid> HeadOfficeAsync(ServiceProvider provider)
    {
        BranchResult result = await Resolve<IBranchService>(provider).CreateAsync(
            new BranchRequest
            {
                Name = "Lagos HQ",
                Code = "LHQ",
                Kind = BranchKind.Headquarters,
                Status = BranchStatus.Active,
                AddressLines = ["12 Marina Road"],
                City = "Lagos",
                Region = "Lagos State",
                Country = "Nigeria",
            });

        Assert.True(result.Succeeded, $"{result.Outcome}");

        return result.Branch!.Id;
    }

    // Number and certificate in place, then handed over.
    private async Task<CompanyResult<VerificationDto>> ReadyAsync(ServiceProvider provider)
    {
        CompanyResult<VerificationDto> saved = await Verification(provider).SaveDetailsAsync(
            VerificationKind.Registration,
            new VerificationDetailsRequest
            {
                Number = "RC-123456",
                DocumentFileId = await CertificateAsync(),
            });

        Assert.True(saved.Succeeded, $"{saved.Outcome} / {saved.Refusal}");

        return await Verification(provider).SubmitAsync(VerificationKind.Registration);
    }

    // A real stored_files row. The column is a foreign key, so a made-up id is
    // refused by the database — which is the behaviour we want: a verification
    // pointing at a document that does not exist is a verification of nothing.
    private async Task<Guid> CertificateAsync()
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Guid fileId = Guid.NewGuid();

        seed.StoredFiles.Add(new Motee.Domain.Files.StoredFile
        {
            Id = fileId,
            TenantId = _tenantId,
            Purpose = Motee.Domain.Files.FilePurpose.CompanyVerificationDocument,
            FileName = "certificate-of-incorporation.pdf",
            ContentType = "application/pdf",
            SizeBytes = 2048,
            StorageKey = $"company/{_tenantId}/{fileId}.pdf",
            UploadedAt = _clock.GetUtcNow(),
        });

        await seed.SaveChangesAsync();

        return fileId;
    }

    private Task<CompanyResult<VerificationDto>> RejectAsync(ServiceProvider provider) =>
        Verification(provider).ReviewAsync(
            _tenantId,
            VerificationKind.Registration,
            new VerificationReviewRequest
            {
                Stage = VerificationStage.Rejected,
                Note = "The certificate is for another company.",
            });

    private static async Task PinAsync(ServiceProvider provider, string title)
    {
        IAnnouncementService announcements = Resolve<IAnnouncementService>(provider);

        AnnouncementResult created = await announcements.CreateAsync(new AnnouncementRequest
        {
            Title = title,
            Body = "All managers must complete team reviews by 30 April.",
            Type = AnnouncementType.General,
            Audience = AnnouncementAudience.AllStaff,
            IsPinned = true,
        });

        Assert.True(created.Succeeded, $"{created.Outcome}");

        // Created is a draft. The company profile shows what is actually live,
        // so it has to be published before it counts as the pinned notice.
        AnnouncementResult published =
            await announcements.PublishAsync(created.Announcement!.Id);

        Assert.True(published.Succeeded, $"{published.Outcome}");
    }

    private async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        Guid? managerId = null) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            ManagerId = managerId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;
}
