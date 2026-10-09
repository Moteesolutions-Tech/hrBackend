using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Approvals;
using Motee.Application.Files;
using Motee.Domain.Approvals;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Files;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// Evidence a chain expects to be shown before anybody decides. A fit note, a signed
// contract, a receipt — the thing that makes an approval a judgement rather than a
// rubber stamp.
[Collection(PostgresCollection.Name)]
public class ApprovalAttachmentTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _subjectId;
    private Guid _headUserId;

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        _tenantId = Guid.NewGuid();
        _subjectId = Guid.NewGuid();
        _headUserId = Guid.NewGuid();

        Guid headId = Guid.NewGuid();
        Guid departmentId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = _tenantId,
                Name = "Acme",
                Slug = $"acme-{_tenantId:N}",
                CountryCode = CountryCode.Nigeria,
            });

            await seed.SaveChangesAsync();
        }

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.Employees.Add(new Employee
            {
                Id = headId,
                TenantId = _tenantId,
                FirstName = "Cara",
                LastName = "Nwosu",
                Email = "cara@acme.com",
                Status = EmployeeStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            seed.Users.Add(new ApplicationUser
            {
                Id = _headUserId,
                TenantId = _tenantId,
                EmployeeId = headId,
                FirstName = "Cara",
                LastName = "Nwosu",
                Email = "cara@acme.com",
                NormalizedEmail = "CARA@ACME.COM",
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            seed.Departments.Add(new Department
            {
                Id = departmentId,
                TenantId = _tenantId,
                Name = "Engineering",
                Code = "ENG",
                HeadEmployeeId = headId,
                Status = DepartmentStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            seed.Employees.Add(new Employee
            {
                Id = _subjectId,
                TenantId = _tenantId,
                FirstName = "Ben",
                LastName = "Adeyemi",
                Email = "ben@acme.com",
                DepartmentId = departmentId,
                Status = EmployeeStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            _headUserId.ToString();

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static async Task TemplateAsync(
        ServiceProvider provider,
        bool allowed,
        bool required,
        string? note = null)
    {
        ApprovalTemplateResult created = await Resolve<IApprovalTemplateService>(provider)
            .CreateAsync(new ApprovalTemplateRequest
            {
                DocumentType = ApprovalDocumentTypes.LeaveRequest,
                Name = "Sick leave",
                IsDefault = true,
                Attachments = new AttachmentRules
                {
                    Allowed = allowed,
                    Required = required,
                    Note = note,
                },
                Steps =
                [
                    new ApprovalTemplateStepRequest
                    {
                        Label = "Department head approval",
                        Approver = ApproverResolver.DepartmentHead,
                        Required = true,
                    },
                ],
            });

        Assert.True(created.Succeeded, created.Outcome.ToString());
    }

    private static async Task<Guid> UploadAsync(
        ServiceProvider provider,
        string name = "fit-note.pdf",
        FilePurpose purpose = FilePurpose.ApprovalAttachment)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("%PDF-1.4 fit note");

        FileUploadResult result = await Resolve<IFileUploadService>(provider).UploadAsync(
            new FileUpload
            {
                Purpose = purpose,
                FileName = name,
                ContentType = "application/pdf",
                SizeBytes = bytes.Length,
                Content = new MemoryStream(bytes),
            });

        Assert.True(result.Succeeded, result.Rejection.ToString());

        return result.File!.Id;
    }

    private Task<ApprovalResult> StartAsync(ServiceProvider provider, params Guid[] fileIds) =>
        Resolve<IApprovalService>(provider).StartAsync(new StartApprovalRequest
        {
            DocumentType = ApprovalDocumentTypes.LeaveRequest,
            SubjectType = "LeaveRequest",
            SubjectId = Guid.NewGuid(),
            SubjectEmployeeId = _subjectId,
            FileIds = fileIds,
        });

    // The point of the whole feature: an approver sees what they are approving against.
    [SkippableFact]
    public async Task AnAttachedFileIsCarriedOnTheApproval()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider, allowed: true, required: true);

        Guid fileId = await UploadAsync(provider);

        ApprovalResult started = await StartAsync(provider, fileId);

        Assert.True(started.Succeeded, started.Outcome.ToString());

        ApprovalAttachmentDto attachment = Assert.Single(started.Approval!.Attachments);

        Assert.Equal(fileId, attachment.FileId);
        Assert.Equal("fit-note.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.True(attachment.SizeBytes > 0);
        Assert.Equal("Cara Nwosu", attachment.UploadedByName);
    }

    // Signed on read, and carrying the real filename — an attachment is something
    // somebody downloads and keeps, unlike an avatar which is rendered in place.
    [SkippableFact]
    public async Task TheLinkIsSignedAndNamed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider, allowed: true, required: false);

        ApprovalResult started = await StartAsync(provider, await UploadAsync(provider));

        ApprovalAttachmentDto attachment = Assert.Single(started.Approval!.Attachments);

        Assert.NotNull(attachment.Url);
        Assert.Contains("fit-note.pdf", attachment.Url, StringComparison.Ordinal);
    }

    // Refused while the person holding the fit note is still at the keyboard, rather
    // than left for an approver to discover days later.
    [SkippableFact]
    public async Task AChainThatInsistsOnEvidenceRefusesWithoutIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await TemplateAsync(
            provider, allowed: true, required: true,
            note: "Fit note for absences over 7 days.");

        ApprovalResult started = await StartAsync(provider);

        Assert.Equal(ApprovalOutcome.AttachmentRequired, started.Outcome);

        // And it says what was wanted. "Attachment required" alone tells somebody they
        // are missing something but not what, which is how a required field becomes a
        // guess.
        Assert.Equal("Fit note for absences over 7 days.", started.Reason);
    }

    [SkippableFact]
    public async Task AChainThatWantsNoEvidenceRefusesFilesRatherThanDroppingThem()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider, allowed: false, required: false);

        ApprovalResult started = await StartAsync(provider, await UploadAsync(provider));

        Assert.Equal(ApprovalOutcome.AttachmentNotAllowed, started.Outcome);
    }

    // Allowed but not required is the ordinary middle case: attach if you have something.
    [SkippableFact]
    public async Task OptionalEvidenceCanBeOmitted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider, allowed: true, required: false);

        ApprovalResult started = await StartAsync(provider);

        Assert.True(started.Succeeded, started.Outcome.ToString());
        Assert.Empty(started.Approval!.Attachments);
    }

    // A file uploaded for something else — an avatar, an employee document — is not
    // evidence somebody chose to attach here, and treating it as such would let one
    // module reach into another's files.
    [SkippableFact]
    public async Task AFileUploadedForSomethingElseIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider, allowed: true, required: true);

        Guid documentId = await UploadAsync(
            provider, "contract.pdf", FilePurpose.EmployeeDocument);

        ApprovalResult started = await StartAsync(provider, documentId);

        Assert.Equal(ApprovalOutcome.AttachmentNotAllowed, started.Outcome);
    }

    // Refusing must leave nothing behind. Half an approval committed by an unrelated
    // save later in the same scope would be an approval nobody asked for.
    [SkippableFact]
    public async Task ARefusedSubmissionCreatesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider, allowed: true, required: true);

        Assert.Equal(ApprovalOutcome.AttachmentRequired, (await StartAsync(provider)).Outcome);

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        Assert.Empty(await context.ApprovalInstances.ToListAsync());
        Assert.Empty(await context.ApprovalAttachments.ToListAsync());
    }

    // The rules travel with the approval, so a screen can say what is expected before
    // somebody submits rather than after it is refused.
    [SkippableFact]
    public async Task TheRulesAreVisibleOnTheApproval()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await TemplateAsync(
            provider, allowed: true, required: true, note: "Fit note for absences over 7 days.");

        ApprovalResult started = await StartAsync(provider, await UploadAsync(provider));

        AttachmentRules rules = started.Approval!.AttachmentRules;

        Assert.True(rules.Required);
        Assert.Equal("Fit note for absences over 7 days.", rules.Note);
    }

    // Evidence hangs on the run, not on a step, so every approver reads the same set.
    // And a note that arrives only on the second round reads as exactly that.
    [SkippableFact]
    public async Task EvidenceIsStampedWithTheRoundItArrivedIn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();
        await TemplateAsync(provider, allowed: true, required: false);

        ApprovalResult started = await StartAsync(provider, await UploadAsync(provider));

        ApprovalAttachmentDto attachment = Assert.Single(started.Approval!.Attachments);

        Assert.Equal(started.Approval.Round, attachment.Round);
    }

    // The template screen has to show what it saved, or an admin cannot tell whether the
    // rule took.
    [SkippableFact]
    public async Task TheTemplateReportsItsOwnRules()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await TemplateAsync(provider, allowed: true, required: true, note: "Receipt required.");

        ApprovalTemplateDto template = Assert.Single(
            await Resolve<IApprovalTemplateService>(provider)
                .ListAsync(ApprovalDocumentTypes.LeaveRequest));

        Assert.True(template.Attachments.Allowed);
        Assert.True(template.Attachments.Required);
        Assert.Equal("Receipt required.", template.Attachments.Note);
    }

    // Nothing configured means nothing expected. A chain written before this feature
    // existed must not start refusing submissions.
    [SkippableFact]
    public async Task AChainWithNoRulesIsUnaffected()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalTemplateResult created = await Resolve<IApprovalTemplateService>(provider)
            .CreateAsync(new ApprovalTemplateRequest
            {
                DocumentType = ApprovalDocumentTypes.LeaveRequest,
                Name = "Plain",
                IsDefault = true,
                Steps =
                [
                    new ApprovalTemplateStepRequest
                    {
                        Label = "Department head approval",
                        Approver = ApproverResolver.DepartmentHead,
                        Required = true,
                    },
                ],
            });

        Assert.True(created.Succeeded, created.Outcome.ToString());
        Assert.False(created.Template!.Attachments.Permits);

        ApprovalResult started = await StartAsync(provider);

        Assert.True(started.Succeeded, started.Outcome.ToString());
    }
}
