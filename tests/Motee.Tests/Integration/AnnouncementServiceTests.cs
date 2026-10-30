using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Announcements;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Announcements;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// The company noticeboard.
//
// Written before the service. The rule doing the most work is that scheduling
// needs no job: an announcement dated for Monday becomes visible on Monday
// because the date is the answer, not because something ran. The feed stays
// correct with every worker down, and nothing has to be flipped at midnight.
[Collection(PostgresCollection.Name)]
public class AnnouncementServiceTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _employeeId;
    private Guid _managerId;
    private Guid _departmentId;
    private Guid _otherDepartmentId;

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = "ada@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        IDepartmentService departments = Resolve<IDepartmentService>(provider);

        _departmentId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _otherDepartmentId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "Sales", Code = "SAL" }))
            .Department!.Id;

        // Cara manages Bola, which is what makes one of them a manager and the
        // other not — both derived from the reporting line rather than a flag.
        _managerId = await HireAsync(provider, _departmentId, "Cara", "cara@acme.com");
        _employeeId = await HireAsync(
            provider, _departmentId, "Bola", "bola@acme.com", _managerId);

        await ActAsAsync(provider, _employeeId, "bola");

        return provider;
    }

    private async Task ActAsAsync(ServiceProvider provider, Guid employeeId, string handle)
    {
        Guid userId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
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
        }

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();
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

    // An upload that has already happened. Seeded directly rather than through
    // the upload endpoint, which has its own tests and would put S3 in the way.
    private async Task<Guid> PdfAsync()
    {
        Guid id = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.StoredFiles.Add(new Motee.Domain.Files.StoredFile
        {
            Id = id,
            TenantId = _tenantId,
            Purpose = Motee.Domain.Files.FilePurpose.CompanyDocument,
            FileName = "handbook-2026.pdf",
            ContentType = "application/pdf",
            SizeBytes = 2048,
            StorageKey = $"announcements/{id}",
            UploadedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();

        return id;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static AnnouncementRequest Notice(
        AnnouncementAudience audience = AnnouncementAudience.AllStaff,
        bool requiresAcknowledgement = false,
        DateTimeOffset? scheduledFor = null,
        DateTimeOffset? expiresAt = null) => new()
    {
        Title = "Office closed on Friday",
        Body = "The building is shut for maintenance.",
        Type = AnnouncementType.General,
        Audience = audience,
        RequiresAcknowledgement = requiresAcknowledgement,
        ScheduledFor = scheduledFor,
        ExpiresAt = expiresAt,
    };

    private static async Task<Guid> PublishedAsync(
        ServiceProvider provider,
        AnnouncementRequest request)
    {
        IAnnouncementService announcements = Resolve<IAnnouncementService>(provider);

        AnnouncementResult created = await announcements.CreateAsync(request);

        Assert.True(created.Succeeded, created.Outcome.ToString());

        AnnouncementResult published = await announcements.PublishAsync(created.Announcement!.Id);

        Assert.True(published.Succeeded, published.Outcome.ToString());

        return published.Announcement!.Id;
    }

    [SkippableFact]
    public async Task ANewAnnouncementIsADraftNobodySees()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAnnouncementService announcements = Resolve<IAnnouncementService>(provider);

        AnnouncementDto created = (await announcements.CreateAsync(Notice())).Announcement!;

        Assert.Equal("draft", created.Status);
        Assert.Empty(await announcements.FeedAsync());
    }

    [SkippableFact]
    public async Task PublishingPutsItInTheFeed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await PublishedAsync(provider, Notice());

        AnnouncementDto seen = Assert.Single(
            await Resolve<IAnnouncementService>(provider).FeedAsync());

        Assert.Equal("published", seen.Status);
        Assert.NotNull(seen.PublishedAt);
    }

    // The rule that removes a job: the date is the answer.
    [SkippableFact]
    public async Task AnAnnouncementDatedInTheFutureIsNotVisibleYet()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAnnouncementService announcements = Resolve<IAnnouncementService>(provider);

        AnnouncementDto scheduled = (await announcements.CreateAsync(
            Notice(scheduledFor: DateTimeOffset.UtcNow.AddDays(2)))).Announcement!;

        Assert.Equal("scheduled", scheduled.Status);
        Assert.Empty(await announcements.FeedAsync());
    }

    // And becomes visible on its own, with nothing having run.
    [SkippableFact]
    public async Task AnAnnouncementWhoseTimeHasComeIsVisibleWithoutAnyJob()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAnnouncementService announcements = Resolve<IAnnouncementService>(provider);

        await announcements.CreateAsync(
            Notice(scheduledFor: DateTimeOffset.UtcNow.AddMinutes(-5)));

        AnnouncementDto live = Assert.Single(await announcements.FeedAsync());

        Assert.Equal("published", live.Status);
    }

    [SkippableFact]
    public async Task AnExpiredAnnouncementDropsOutOfTheFeed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAnnouncementService announcements = Resolve<IAnnouncementService>(provider);

        Guid id = await PublishedAsync(
            provider, Notice(expiresAt: DateTimeOffset.UtcNow.AddHours(-1)));

        Assert.Empty(await announcements.FeedAsync());
        Assert.Equal("expired", (await announcements.GetAsync(id))!.Status);
    }

    [SkippableFact]
    public async Task AnAnnouncementExpiringBeforeItAppearsIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            AnnouncementOutcome.ExpiresBeforeItAppears,
            (await Resolve<IAnnouncementService>(provider).CreateAsync(Notice(
                scheduledFor: DateTimeOffset.UtcNow.AddDays(3),
                expiresAt: DateTimeOffset.UtcNow.AddDays(1)))).Outcome);
    }

    // ── audience ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ADepartmentNoticeReachesThatDepartmentOnly()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAnnouncementService announcements = Resolve<IAnnouncementService>(provider);

        await PublishedAsync(provider, Notice(AnnouncementAudience.Department) with
        {
            TargetDepartmentIds = [_otherDepartmentId],
        });

        // Bola is in Engineering; the notice went to Sales.
        Assert.Empty(await announcements.FeedAsync());
    }

    [SkippableFact]
    public async Task ADepartmentNoticeWithNoDepartmentsIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            AnnouncementOutcome.DepartmentsRequired,
            (await Resolve<IAnnouncementService>(provider)
                .CreateAsync(Notice(AnnouncementAudience.Department))).Outcome);
    }

    // Derived from the reporting line, so it stays right as teams change.
    [SkippableFact]
    public async Task AManagersNoticeReachesPeopleWithReportsAndNobodyElse()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await PublishedAsync(provider, Notice(AnnouncementAudience.Managers));

        // Bola manages nobody.
        Assert.Empty(await Resolve<IAnnouncementService>(provider).FeedAsync());

        // Cara manages Bola.
        await ActAsAsync(provider, _managerId, "cara");

        Assert.Single(await Resolve<IAnnouncementService>(provider).FeedAsync());
    }

    // Leadership is defined here as the top of the tree — people with no manager
    // of their own — because the product has no other notion of seniority.
    [SkippableFact]
    public async Task ALeadershipNoticeReachesThoseWithNoManager()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await PublishedAsync(provider, Notice(AnnouncementAudience.Leadership));

        // Bola reports to Cara.
        Assert.Empty(await Resolve<IAnnouncementService>(provider).FeedAsync());

        // Cara reports to nobody.
        await ActAsAsync(provider, _managerId, "cara");

        Assert.Single(await Resolve<IAnnouncementService>(provider).FeedAsync());
    }

    // ── reading and acknowledging ───────────────────────────────────────────

    // Unique readers rather than clicks, so refreshing does not inflate it.
    [SkippableFact]
    public async Task ViewingTwiceCountsOnce()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAnnouncementService announcements = Resolve<IAnnouncementService>(provider);
        Guid id = await PublishedAsync(provider, Notice());

        await announcements.MarkViewedAsync(id);
        await announcements.MarkViewedAsync(id);

        Assert.Equal(1, (await announcements.GetAsync(id))!.ViewCount);
    }

    [SkippableFact]
    public async Task SomebodyAcknowledgesANoticeThatAsksForIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAnnouncementService announcements = Resolve<IAnnouncementService>(provider);

        Guid id = await PublishedAsync(provider, Notice(requiresAcknowledgement: true));

        Assert.False(Assert.Single(await announcements.FeedAsync()).AcknowledgedByMe);

        Assert.True((await announcements.AcknowledgeAsync(id)).Succeeded);
        Assert.True(Assert.Single(await announcements.FeedAsync()).AcknowledgedByMe);
    }

    [SkippableFact]
    public async Task NobodyAcknowledgesTwice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAnnouncementService announcements = Resolve<IAnnouncementService>(provider);

        Guid id = await PublishedAsync(provider, Notice(requiresAcknowledgement: true));

        await announcements.AcknowledgeAsync(id);

        Assert.Equal(
            AnnouncementOutcome.AlreadyAcknowledgedByYou,
            (await announcements.AcknowledgeAsync(id)).Outcome);
    }

    [SkippableFact]
    public async Task AcknowledgingSomethingThatAsksForNoneIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAnnouncementService announcements = Resolve<IAnnouncementService>(provider);

        Guid id = await PublishedAsync(provider, Notice());

        Assert.Equal(
            AnnouncementOutcome.AcknowledgementNotRequired,
            (await announcements.AcknowledgeAsync(id)).Outcome);
    }

    // Nobody agrees to something they were never shown.
    [SkippableFact]
    public async Task ADraftCannotBeAcknowledged()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAnnouncementService announcements = Resolve<IAnnouncementService>(provider);

        Guid id = (await announcements.CreateAsync(Notice(requiresAcknowledgement: true)))
            .Announcement!.Id;

        Assert.Equal(
            AnnouncementOutcome.NotAvailable, (await announcements.AcknowledgeAsync(id)).Outcome);
    }

    // Their agreement was to what it said then.
    [SkippableFact]
    public async Task ANoticePeopleHaveAgreedToCannotBeEdited()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAnnouncementService announcements = Resolve<IAnnouncementService>(provider);

        Guid id = await PublishedAsync(provider, Notice(requiresAcknowledgement: true));

        await announcements.AcknowledgeAsync(id);

        Assert.Equal(
            AnnouncementOutcome.AlreadyAcknowledged,
            (await announcements.UpdateAsync(
                id, Notice(requiresAcknowledgement: true) with { Body = "Changed." })).Outcome);
    }

    // "Fourteen outstanding" is a number; a list is something somebody can chase.
    [SkippableFact]
    public async Task TheSummaryNamesWhoHasNotAgreedYet()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAnnouncementService announcements = Resolve<IAnnouncementService>(provider);

        Guid id = await PublishedAsync(provider, Notice(requiresAcknowledgement: true));

        await announcements.AcknowledgeAsync(id);

        AcknowledgementSummaryDto summary = (await announcements.AcknowledgementsAsync(id))!;

        Assert.Equal(2, summary.TotalTargeted);
        Assert.Equal("Bola Okafor", Assert.Single(summary.Acknowledged).EmployeeName);
        Assert.Equal("Cara Okafor", Assert.Single(summary.Outstanding));
    }

    // Held at the top whatever its date, which is what pinning is for.
    [SkippableFact]
    public async Task APinnedNoticeLeadsTheFeed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await PublishedAsync(provider, Notice());
        await PublishedAsync(provider, Notice() with { Title = "Pinned", IsPinned = true });

        IReadOnlyList<AnnouncementDto> feed =
            await Resolve<IAnnouncementService>(provider).FeedAsync();

        Assert.Equal("Pinned", feed[0].Title);
    }

    [SkippableFact]
    public async Task AnArchivedNoticeLeavesTheFeedAndTheDefaultList()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAnnouncementService announcements = Resolve<IAnnouncementService>(provider);

        Guid id = await PublishedAsync(provider, Notice());

        await announcements.ArchiveAsync(id);

        Assert.Empty(await announcements.FeedAsync());
        Assert.Empty((await announcements.ListAsync(new AnnouncementQuery())).Items);
        Assert.Single((await announcements.ListAsync(
            new AnnouncementQuery { IncludeArchived = true })).Items);
    }

    // ── what the screen needs that it cannot work out ───────────────────────
    //
    // Three pieces the client has no way to derive: a department id is not a
    // name, a file id is not a file name, and an employee id on an
    // acknowledgement is not a person. Everything else on the frontend's type —
    // initials, isArchived — it already computes from what is sent.

    [SkippableFact]
    public async Task ADepartmentNoticeNamesTheDepartmentsItWentTo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await PublishedAsync(provider, Notice(AnnouncementAudience.Department) with
        {
            TargetDepartmentIds = [_departmentId, _otherDepartmentId],
        });

        AnnouncementDto seen = (await Resolve<IAnnouncementService>(provider).GetAsync(id))!;

        Assert.Equal(["Engineering", "Sales"], seen.TargetDepartments.Order(StringComparer.Ordinal));
    }

    [SkippableFact]
    public async Task AnAttachmentIsNamedRatherThanOnlyIdentified()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid fileId = await PdfAsync();

        Guid id = await PublishedAsync(
            provider, Notice() with { AttachmentFileId = fileId });

        Assert.Equal(
            "handbook-2026.pdf",
            (await Resolve<IAnnouncementService>(provider).GetAsync(id))!.AttachmentName);
    }

    [SkippableFact]
    public async Task TheAcknowledgementsSayWhoAndFromWhere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAnnouncementService announcements = Resolve<IAnnouncementService>(provider);

        Guid id = await PublishedAsync(provider, Notice(requiresAcknowledgement: true));

        await announcements.AcknowledgeAsync(id);

        AcknowledgedPersonDto agreed =
            Assert.Single((await announcements.GetAsync(id))!.Acknowledgements);

        Assert.Equal("Bola Okafor", agreed.EmployeeName);
        Assert.Equal("Engineering", agreed.Department);
    }

    [SkippableFact]
    public async Task ANoticeNeedsATitleAndABody()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAnnouncementService announcements = Resolve<IAnnouncementService>(provider);

        Assert.Equal(
            AnnouncementOutcome.TitleRequired,
            (await announcements.CreateAsync(Notice() with { Title = " " })).Outcome);

        Assert.Equal(
            AnnouncementOutcome.BodyRequired,
            (await announcements.CreateAsync(Notice() with { Body = " " })).Outcome);
    }
}
