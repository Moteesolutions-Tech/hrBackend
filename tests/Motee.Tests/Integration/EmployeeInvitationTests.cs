using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Authorization;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Files;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class EmployeeInvitationTests(PostgresFixture fixture)
{
    private async Task<(Guid TenantId, Guid DepartmentId, ServiceProvider Provider)> ArrangeAsync()
    {
        await fixture.ResetAsync();

        Guid tenantId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Acme Corporation",
                Slug = $"acme-{tenantId:N}",
                CountryCode = CountryCode.Nigeria,
            });

            await seed.SaveChangesAsync();
        }

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = tenantId;

        DepartmentResult department = await provider.CreateScope().ServiceProvider
            .GetRequiredService<IDepartmentService>()
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" });

        return (tenantId, department.Department!.Id, provider);
    }

    private static IEmployeeInvitationService Invites(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IEmployeeInvitationService>();

    private static InviteRequest Request(Guid departmentId, string email = "ada@acme.com") => new()
    {
        FirstName = "Ada",
        LastName = "Okafor",
        Email = email,
        JobTitle = "Engineer",
        DepartmentId = departmentId,
        EmploymentType = EmploymentType.FullTime,
        StartDate = new DateOnly(2026, 9, 1),
    };

    private static AcceptInviteRequest Accept(string password = "correct-horse") => new()
    {
        Password = password,
        Phone = "08012345678",
        Nationality = "Nigerian",
        EmergencyContactName = "Bola Okafor",
        EmergencyContactRelationship = "Sister",
        EmergencyContactPhone = "08087654321",
    };

    // The record exists whether or not they ever open the link, so HR sees them in
    // the pipeline immediately.
    [SkippableFact]
    public async Task InvitingCreatesAPendingEmployee()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult result = await Invites(owned).InviteAsync(Request(departmentId));

        Assert.True(result.Succeeded, result.Outcome.ToString());
        Assert.Equal(EmployeeStatus.Pending, result.Employee!.Status);
        Assert.Equal(OnboardingMethod.Invite, result.Employee.OnboardingMethod);
        Assert.NotNull(result.Token);
    }

    // A relative path is not clickable in a mail client, and the host cannot come
    // from the request or a forged Host header would redirect the invitation.
    [SkippableFact]
    public async Task TheInviteEmailCarriesAnAbsoluteJoinLink()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        Guid tenantId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Acme Corporation",
                Slug = $"acme-{tenantId:N}",
                CountryCode = CountryCode.Nigeria,
            });

            await seed.SaveChangesAsync();
        }

        RecordingEmailSender mail = new();
        await using ServiceProvider owned = fixture.BuildProvider(services =>
            services.AddScoped<IEmailSender>(_ => mail));

        owned.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = tenantId;

        Guid departmentId = (await owned.CreateScope().ServiceProvider
            .GetRequiredService<IDepartmentService>()
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));

        Assert.Contains($"https://app.test/join/{invite.Token}", mail.Sent.Single().Body,
            StringComparison.Ordinal);
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    // ---- self-onboarding ----

    // Everything the admin set at invite time comes back so the wizard can show it
    // filled and disabled.
    [SkippableFact]
    public async Task ThePreviewCarriesEveryFieldTheAdminSet()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(
            Request(departmentId) with { MiddleName = "Ngozi" });

        InvitationPreview preview = (await Invites(owned).PreviewAsync(invite.Token!))!;

        Assert.Equal("Ada", preview.FirstName);
        Assert.Equal("Ngozi", preview.MiddleName);
        Assert.Equal("Engineering", preview.Department);
        Assert.Equal(departmentId, preview.DepartmentId);
        Assert.Equal(EmploymentType.FullTime, preview.EmploymentType);
        Assert.Equal(new DateOnly(2026, 9, 1), preview.StartDate);
        Assert.Equal("Acme Corporation", preview.CompanyName);
    }

    // The joiner fills the same wizard the manual flow uses, including the blocks.
    [SkippableFact]
    public async Task TheJoinerFillsTheSameProfileAsTheManualWizard()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));

        AcceptInviteResult accepted = await Invites(owned).AcceptAsync(
            invite.Token!,
            Accept() with
            {
                Title = "Ms",
                Initials = "AO",
                EmergencyContactEmail = "bola@example.com",
                BankDetails = new BankDetailsRequest { AccountNumber = "0123456789" },
                IdentityDocuments = new IdentityDocumentsRequest { NationalIdNumber = "01234567890" },
                Medical = new MedicalRequest { Allergies = "Peanuts" },
            });

        Assert.True(accepted.Succeeded, accepted.Outcome.ToString());

        IEmployeeService employees = owned.CreateScope().ServiceProvider
            .GetRequiredService<IEmployeeService>();

        EmployeeDto employee = (await employees.GetAsync(invite.Employee!.Id))!;

        Assert.Equal("Ms", employee.Title);
        Assert.Equal("AO", employee.Initials);
        Assert.Equal("bola@example.com", employee.EmergencyContactEmail);
        Assert.Equal("0123456789", employee.BankDetails!.AccountNumber);
        Assert.Equal("01234567890", employee.IdentityDocuments!.NationalIdNumber);
        Assert.Equal("Peanuts", (await employees.GetMedicalAsync(employee.Id))!.Allergies);
    }

    // The blocks are written while nobody is signed in. Stamped with the wrong tenant
    // they would be invisible to the company that owns them.
    [SkippableFact]
    public async Task WhatTheJoinerWritesBelongsToTheirCompany()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));

        // Nothing resolves a tenant for a joiner, exactly as in production.
        owned.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = null;

        await Invites(owned).AcceptAsync(
            invite.Token!,
            Accept() with { BankDetails = new BankDetailsRequest { AccountNumber = "0123456789" } });

        await using MoteeDbContext context = fixture.CreateContext(tenantId);

        Assert.Equal(tenantId, (await context.EmployeeBankDetails.SingleAsync()).TenantId);
    }

    // A disabled input is a UI courtesy. The contract has no field for these at all,
    // and what the admin set has to survive whatever arrives.
    [SkippableFact]
    public async Task TheJoinerCannotChangeWhatTheAdminSet()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid otherDepartment = (await owned.CreateScope().ServiceProvider
            .GetRequiredService<IDepartmentService>()
            .CreateAsync(new DepartmentRequest { Name = "Finance", Code = "FIN" }))
            .Department!.Id;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));

        await Invites(owned).AcceptAsync(invite.Token!, Accept());

        EmployeeDto employee = (await owned.CreateScope().ServiceProvider
            .GetRequiredService<IEmployeeService>()
            .GetAsync(invite.Employee!.Id))!;

        Assert.Equal(departmentId, employee.DepartmentId);
        Assert.NotEqual(otherDepartment, employee.DepartmentId);
        Assert.Equal("Engineer", employee.JobTitle);
        Assert.Equal(EmploymentType.FullTime, employee.EmploymentType);
        Assert.Equal(new DateOnly(2026, 9, 1), employee.StartDate);
    }

    // The wizard is several steps long; a photo that only lands on submit is lost if
    // they close the tab.
    [SkippableFact]
    public async Task TheJoinerCanUploadAPhotoBeforeSubmitting()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));

        // No tenant resolved, exactly as for a real joiner.
        owned.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = null;

        JoinPhotoResult photo = await Invites(owned).UploadPhotoAsync(invite.Token!, Png());

        Assert.True(photo.Succeeded, photo.Outcome.ToString());

        owned.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = tenantId;

        EmployeeDto employee = (await owned.CreateScope().ServiceProvider
            .GetRequiredService<IEmployeeService>()
            .GetAsync(invite.Employee!.Id))!;

        Assert.Equal(photo.FileId, employee.AvatarFileId);
    }

    // The frontend gets a link it can put straight into an <img>, minted on read
    // rather than stored — a saved URL would be a broken image a week later.
    [SkippableFact]
    public async Task ReadingAnEmployeeMintsAFreshPhotoLink()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));

        owned.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = null;
        await Invites(owned).UploadPhotoAsync(invite.Token!, Png());
        owned.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = tenantId;

        IEmployeeService employees = owned.CreateScope().ServiceProvider
            .GetRequiredService<IEmployeeService>();

        EmployeeDto employee = (await employees.GetAsync(invite.Employee!.Id))!;

        Assert.NotNull(employee.AvatarUrl);
        Assert.Contains("avatars/", employee.AvatarUrl, StringComparison.Ordinal);

        // The list the table renders carries it too, or every row would need its own
        // request to show a face.
        EmployeeListItemDto row = (await employees.ListAsync(
            new EmployeeQuery { Scope = DataScope.Everything })).Items.Single();

        Assert.Equal(employee.AvatarUrl, row.AvatarUrl);
    }

    // Nothing to sign, and no stray query for a page of people who have no photo.
    [SkippableFact]
    public async Task SomeoneWithNoPhotoHasNoLink()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));

        EmployeeDto employee = (await owned.CreateScope().ServiceProvider
            .GetRequiredService<IEmployeeService>()
            .GetAsync(invite.Employee!.Id))!;

        Assert.Null(employee.AvatarFileId);
        Assert.Null(employee.AvatarUrl);
    }

    [SkippableFact]
    public async Task ARevokedLinkCannotUploadAPhoto()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));
        await Invites(owned).RevokeAsync(invite.Employee!.Id);

        JoinPhotoResult photo = await Invites(owned).UploadPhotoAsync(invite.Token!, Png());

        Assert.False(photo.Succeeded);
        Assert.Equal(InvitationOutcome.Revoked, photo.Outcome);
    }

    // Bytes that contradict the declared type never reach storage.
    [SkippableFact]
    public async Task APdfRenamedToPngIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));

        JoinPhotoResult photo = await Invites(owned).UploadPhotoAsync(
            invite.Token!,
            new JoinPhotoUpload
            {
                FileName = "me.png",
                ContentType = "image/png",
                Content = new MemoryStream("%PDF-1.7 not really a picture"u8.ToArray()),
                SizeBytes = 28,
            });

        Assert.False(photo.Succeeded);
        Assert.Equal(FileRejection.ContentDoesNotMatchType, photo.Rejection);
    }

    private static JoinPhotoUpload Png()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

        return new JoinPhotoUpload
        {
            FileName = "me.png",
            ContentType = "image/png",
            Content = new MemoryStream(png),
            SizeBytes = png.Length,
        };
    }

    // ---- a password link shows nothing HR recorded ----

    // The Send Invite route is the only one where the person completes a profile, so
    // it is the only one that shows them what HR already entered.
    [SkippableFact]
    public async Task TheSendInviteRouteAsksForOnboarding()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));

        InvitationPreview preview = (await Invites(owned).PreviewAsync(invite.Token!))!;

        Assert.Equal(InvitationPurpose.Onboarding, preview.Purpose);
        Assert.Equal("Engineer", preview.JobTitle);
    }

    // Manual Entry and Bulk Upload already captured the record. All that is missing
    // is a password, so the page shows only whose account it is and who set it up.
    [SkippableFact]
    public async Task APasswordLinkRevealsNothingHrEntered()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid employeeId = await HireAsync(owned, departmentId, "manual@acme.com");

        IssueInviteResult issued = await Invites(owned).IssueAsync(employeeId);
        InvitationPreview preview = (await Invites(owned).PreviewAsync(issued.Token!))!;

        Assert.Equal(InvitationPurpose.Credentials, preview.Purpose);

        // Enough to know it is really their account, at a company they recognise.
        Assert.Equal("manual@acme.com", preview.Email);
        Assert.Equal("Acme Corporation", preview.CompanyName);

        // And nothing else.
        Assert.Null(preview.FirstName);
        Assert.Null(preview.LastName);
        Assert.Null(preview.JobTitle);
        Assert.Null(preview.Department);
        Assert.Null(preview.DepartmentId);
        Assert.Null(preview.EmploymentType);
        Assert.Null(preview.StartDate);
    }

    // Not a disabled input — there is nowhere for it to land. A payload that tries to
    // rewrite the record it was never shown changes nothing.
    [SkippableFact]
    public async Task APasswordLinkCannotWriteAProfile()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid employeeId = await HireAsync(owned, departmentId, "manual@acme.com");

        IssueInviteResult issued = await Invites(owned).IssueAsync(employeeId);

        AcceptInviteResult accepted = await Invites(owned).AcceptAsync(
            issued.Token!,
            Accept() with
            {
                Nationality = "Somewhere else",
                BankDetails = new BankDetailsRequest { AccountNumber = "9999999999" },
            });

        Assert.True(accepted.Succeeded, accepted.Outcome.ToString());

        EmployeeDto employee = (await owned.CreateScope().ServiceProvider
            .GetRequiredService<IEmployeeService>()
            .GetAsync(employeeId))!;

        Assert.Null(employee.Nationality);
        Assert.Null(employee.BankDetails);
    }

    // ---- the way in for imported and manually-added staff ----

    // Someone imported from a spreadsheet is Active and has been for years. Giving
    // them an account must not walk their status back to Onboarded.
    [SkippableFact]
    public async Task AnActiveEmployeeKeepsTheirStatusWhenTheyGetAnAccount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid employeeId = await HireAsync(
            owned, departmentId, "imported@acme.com", EmployeeStatus.Active);

        IssueInviteResult issued = await Invites(owned).IssueAsync(employeeId);
        await Invites(owned).AcceptAsync(issued.Token!, Accept());

        EmployeeDto employee = (await owned.CreateScope().ServiceProvider
            .GetRequiredService<IEmployeeService>()
            .GetAsync(employeeId))!;

        Assert.Equal(EmployeeStatus.Active, employee.Status);
    }

    // A genuine new hire still advances.
    [SkippableFact]
    public async Task APendingHireBecomesOnboardedWhenTheyAccept()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));

        await Invites(owned).AcceptAsync(invite.Token!, Accept());

        EmployeeDto employee = (await owned.CreateScope().ServiceProvider
            .GetRequiredService<IEmployeeService>()
            .GetAsync(invite.Employee!.Id))!;

        Assert.Equal(EmployeeStatus.Onboarded, employee.Status);
    }

    // Someone mid-probation who sets up their account is still on probation.
    [SkippableFact]
    public async Task ProbationSurvivesAccountSetup()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid employeeId = await HireAsync(
            owned, departmentId, "probation@acme.com", EmployeeStatus.Probation);

        IssueInviteResult issued = await Invites(owned).IssueAsync(employeeId);
        await Invites(owned).AcceptAsync(issued.Token!, Accept());

        EmployeeDto employee = (await owned.CreateScope().ServiceProvider
            .GetRequiredService<IEmployeeService>()
            .GetAsync(employeeId))!;

        Assert.Equal(EmployeeStatus.Probation, employee.Status);
    }

    // ---- inviting someone who is already on the payroll ----

    private static async Task<Guid> HireAsync(
        ServiceProvider provider,
        Guid departmentId,
        string email = "manual@acme.com",
        EmployeeStatus status = EmployeeStatus.Pending)
    {
        Guid id = (await provider.CreateScope().ServiceProvider
            .GetRequiredService<IEmployeeService>()
            .CreateAsync(new EmployeeRequest
            {
                FirstName = "Manual",
                LastName = "Entry",
                Email = email,
                Phone = "08012345678",
                JobTitle = "Engineer",
                DepartmentId = departmentId,
                EmploymentType = EmploymentType.FullTime,
            })).Employee!.Id;

        if (status != EmployeeStatus.Pending)
        {
            await provider.CreateScope().ServiceProvider
                .GetRequiredService<IEmployeeService>()
                .ChangeStatusAsync(id, status);
        }

        return id;
    }

    // Manual Entry and Bulk Upload create a record with no account. This is the only
    // way those people ever get in.
    [SkippableFact]
    public async Task AnEmployeeAddedWithoutAnInviteCanBeSentOne()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid employeeId = await HireAsync(owned, departmentId);

        IssueInviteResult issued = await Invites(owned).IssueAsync(employeeId);

        Assert.True(issued.Succeeded, issued.Outcome.ToString());
        Assert.NotNull(issued.Token);
        Assert.True((await Invites(owned).PreviewAsync(issued.Token!))!.Valid);
    }

    // After three resends only the newest link should open the door.
    [SkippableFact]
    public async Task ResendingKillsTheEarlierLink()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid employeeId = await HireAsync(owned, departmentId);

        string first = (await Invites(owned).IssueAsync(employeeId)).Token!;
        string second = (await Invites(owned).IssueAsync(employeeId)).Token!;

        Assert.Equal(
            InvitationOutcome.Revoked,
            (await Invites(owned).PreviewAsync(first))!.Outcome);

        Assert.True((await Invites(owned).PreviewAsync(second))!.Valid);
    }

    // The Send Invite route already made an account; a second invitation would try to
    // create another.
    [SkippableFact]
    public async Task SomeoneWhoAlreadyJoinedCannotBeInvitedAgain()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));
        await Invites(owned).AcceptAsync(invite.Token!, Accept());

        Assert.Equal(
            IssueInviteOutcome.AccountExists,
            (await Invites(owned).IssueAsync(invite.Employee!.Id)).Outcome);
    }

    // Inviting a leaver back in is a mistake, not a convenience.
    [SkippableFact]
    public async Task ALeaverCannotBeInvited()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid employeeId = await HireAsync(
            owned, departmentId, status: EmployeeStatus.Offboarding);

        Assert.Equal(
            IssueInviteOutcome.EmployeeNotJoinable,
            (await Invites(owned).IssueAsync(employeeId)).Outcome);
    }

    [SkippableFact]
    public async Task RevokingStopsAnOutstandingLink()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));

        Assert.True((await Invites(owned).RevokeAsync(invite.Employee!.Id)).Succeeded);

        Assert.Equal(
            InvitationOutcome.Revoked,
            (await Invites(owned).PreviewAsync(invite.Token!))!.Outcome);
    }

    [SkippableFact]
    public async Task RevokingWithNothingOutstandingSaysSo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid employeeId = await HireAsync(owned, departmentId);

        Assert.Equal(
            IssueInviteOutcome.NoInvitationOutstanding,
            (await Invites(owned).RevokeAsync(employeeId)).Outcome);
    }

    // The chase list: invited, not joined.
    [SkippableFact]
    public async Task PendingListsWhoHasNotJoinedYet()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult waiting = await Invites(owned).InviteAsync(Request(departmentId));

        InviteResult joined = await Invites(owned)
            .InviteAsync(Request(departmentId, email: "bola@acme.com"));
        await Invites(owned).AcceptAsync(joined.Token!, Accept());

        IReadOnlyList<PendingInvitationDto> pending = await Invites(owned).PendingAsync();

        PendingInvitationDto only = Assert.Single(pending);
        Assert.Equal(waiting.Employee!.Id, only.EmployeeId);
        Assert.Equal("Ada Okafor", only.Name);
        Assert.False(only.Expired);
    }

    [SkippableFact]
    public async Task ARevokedInvitationLeavesTheChaseList()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));
        await Invites(owned).RevokeAsync(invite.Employee!.Id);

        Assert.Empty(await Invites(owned).PendingAsync());
    }

    // Another company's outstanding invitations are not theirs to chase.
    [SkippableFact]
    public async Task PendingIsScopedToTheTenant()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Invites(owned).InviteAsync(Request(departmentId));

        owned.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = Guid.NewGuid();

        Assert.Empty(await Invites(owned).PendingAsync());
    }

    // Only the hash is stored, so a leaked database cannot be used to accept.
    [SkippableFact]
    public async Task TheRawTokenIsNeverStored()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));

        await using MoteeDbContext context = fixture.CreateContext(tenantId);
        EmployeeInvitation stored = await context.EmployeeInvitations.SingleAsync();

        Assert.NotEqual(invite.Token, stored.TokenHash);
        Assert.Equal(64, stored.TokenHash.Length);
    }

    [SkippableFact]
    public async Task PreviewShowsWhatHrAlreadyFilledIn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));
        InvitationPreview? preview = await Invites(owned).PreviewAsync(invite.Token!);

        Assert.NotNull(preview);
        Assert.True(preview.Valid);
        Assert.Equal("Ada", preview.FirstName);
        Assert.Equal("ada@acme.com", preview.Email);
        Assert.Equal("Engineering", preview.Department);
        Assert.Equal("Acme Corporation", preview.CompanyName);
    }

    // The joiner is anonymous, so the token has to reach across the tenant filter.
    [SkippableFact]
    public async Task PreviewWorksWithNoTenantResolved()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));

        owned.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = null;

        Assert.True((await Invites(owned).PreviewAsync(invite.Token!))!.Valid);
    }

    [SkippableFact]
    public async Task AnUnknownTokenPreviewsAsNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Assert.Null(await Invites(owned).PreviewAsync("not-a-real-token"));
        Assert.Null(await Invites(owned).PreviewAsync(""));
    }

    [SkippableFact]
    public async Task AcceptingCreatesTheAccountAndLinksItToTheEmployee()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));
        AcceptInviteResult accepted = await Invites(owned).AcceptAsync(invite.Token!, Accept());

        Assert.True(accepted.Succeeded, accepted.Outcome.ToString());

        await using MoteeDbContext context = fixture.CreateContext(tenantId);
        ApplicationUser user = await context.Users.SingleAsync(candidate => candidate.Email == "ada@acme.com");

        Assert.Equal(invite.Employee!.Id, user.EmployeeId);
        Assert.Equal(tenantId, user.TenantId);
    }

    // Receiving the emailed link is itself proof of mailbox control.
    [SkippableFact]
    public async Task TheNewAccountNeedsNoSeparateEmailVerification()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));
        await Invites(owned).AcceptAsync(invite.Token!, Accept());

        await using MoteeDbContext context = fixture.CreateContext(tenantId);

        Assert.True((await context.Users.SingleAsync()).EmailConfirmed);
    }

    [SkippableFact]
    public async Task AcceptingSignsThemStraightIn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));
        AcceptInviteResult accepted = await Invites(owned).AcceptAsync(invite.Token!, Accept());

        Assert.NotNull(accepted.Session);
        Assert.False(string.IsNullOrWhiteSpace(accepted.Session.AccessToken.Value));
    }

    [SkippableFact]
    public async Task AcceptingCompletesTheProfileAndMovesThemOn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));
        await Invites(owned).AcceptAsync(invite.Token!, Accept());

        EmployeeDto employee = (await provider.CreateScope().ServiceProvider
            .GetRequiredService<IEmployeeService>()
            .GetAsync(invite.Employee!.Id))!;

        Assert.Equal(EmployeeStatus.Onboarded, employee.Status);
        Assert.Equal("Nigerian", employee.Nationality);
        Assert.Equal("Bola Okafor", employee.EmergencyContactName);
    }

    // A forwarded link must not let a second person create an account.
    [SkippableFact]
    public async Task ATokenCannotBeUsedTwice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));
        await Invites(owned).AcceptAsync(invite.Token!, Accept());

        AcceptInviteResult second = await Invites(owned).AcceptAsync(invite.Token!, Accept());

        Assert.Equal(AcceptInviteOutcome.AlreadyUsed, second.Outcome);
    }

    [SkippableFact]
    public async Task AConsumedTokenPreviewsAsUsedRatherThanValid()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));
        await Invites(owned).AcceptAsync(invite.Token!, Accept());

        Assert.Equal(InvitationOutcome.Consumed, (await Invites(owned).PreviewAsync(invite.Token!))!.Outcome);
    }

    [SkippableFact]
    public async Task AnExpiredTokenIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));

        await using (MoteeDbContext context = fixture.CreateContext(tenantId))
        {
            EmployeeInvitation invitation = await context.EmployeeInvitations.SingleAsync();
            invitation.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1);
            await context.SaveChangesAsync();
        }

        Assert.Equal(
            AcceptInviteOutcome.Expired,
            (await Invites(owned).AcceptAsync(invite.Token!, Accept())).Outcome);
    }

    [SkippableFact]
    public async Task AGuessedTokenIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Invites(owned).InviteAsync(Request(departmentId));

        Assert.Equal(
            AcceptInviteOutcome.InvalidToken,
            (await Invites(owned).AcceptAsync("guessed", Accept())).Outcome);
    }

    // The employee id used to be the URL. It must not work as a token.
    [SkippableFact]
    public async Task TheEmployeeIdIsNotAToken()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));

        Assert.Null(await Invites(owned).PreviewAsync(invite.Employee!.Id.ToString()));
    }

    [SkippableFact]
    public async Task AWeakPasswordIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        InviteResult invite = await Invites(owned).InviteAsync(Request(departmentId));

        Assert.Equal(
            AcceptInviteOutcome.WeakPassword,
            (await Invites(owned).AcceptAsync(invite.Token!, Accept("short"))).Outcome);
    }

    [SkippableFact]
    public async Task InvitingSomeoneAlreadyOnStaffIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Invites(owned).InviteAsync(Request(departmentId));

        Assert.Equal(
            EmployeeOutcome.DuplicateEmail,
            (await Invites(owned).InviteAsync(Request(departmentId))).Outcome);
    }
}
