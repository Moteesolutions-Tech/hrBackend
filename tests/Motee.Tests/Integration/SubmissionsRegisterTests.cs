using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Approvals;
using Motee.Application.Common;
using Motee.Domain.Approvals;
using Motee.Domain.Authorization;
using Motee.Domain.Common;
using Motee.Application.Files;
using Motee.Domain.Employees;
using Motee.Domain.Files;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Two lists the engine never had: everything in the tenant, and everything one person
// raised.
//
// The engine already answered "what is waiting on me" and "what is running against this
// record". Neither of those is the Submissions screen, which is a register — every
// request, whoever raised it, whatever became of it. Without it the only way to see a
// submission was to be its next approver, so an approval nobody was waiting on was
// invisible to everybody including the person who sent it.
[Collection(PostgresCollection.Name)]
public class SubmissionsRegisterTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _subjectId;
    private Guid _managerId;
    private Guid _managerUserId;
    private Guid _headId;
    private Guid _headUserId;
    private Guid _outsiderId;
    private Guid _departmentId;

    private async Task<ServiceProvider> ArrangeAsync(bool requiresSignature = false)
    {
        await fixture.ResetAsync();

        _tenantId = Guid.NewGuid();

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

        _managerId = Guid.NewGuid();
        _managerUserId = Guid.NewGuid();
        _headId = Guid.NewGuid();
        _headUserId = Guid.NewGuid();
        _subjectId = Guid.NewGuid();
        _departmentId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            Add(seed, _managerId, _managerUserId, "Ada");

            // Cara reports to Ada. Needed by the intake tests and nothing else: an
            // intake request is about the person raising it, so a line-manager chain
            // resolves against the requester — and a requester with no manager cannot
            // raise one, which is correct and covered separately below.
            Add(seed, _headId, _headUserId, "Cara", managerId: _managerId);

            seed.Departments.Add(new Department
            {
                Id = _departmentId,
                TenantId = _tenantId,
                Name = "Engineering",
                Code = "ENG",
                HeadEmployeeId = _headId,
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
                ManagerId = _managerId,
                DepartmentId = _departmentId,
                Status = EmployeeStatus.Active,
                OnboardingMethod = OnboardingMethod.Manual,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        await TemplateAsync(provider, ApprovalDocumentTypes.Onboarding, requiresSignature);
        await TemplateAsync(provider, ApprovalDocumentTypes.LeaveRequest, requiresSignature);

        return provider;
    }

    private void Add(
        MoteeDbContext seed,
        Guid employeeId,
        Guid userId,
        string first,
        Guid? managerId = null)
    {
        seed.Employees.Add(new Employee
        {
            Id = employeeId,
            TenantId = _tenantId,
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            ManagerId = managerId,
            Status = EmployeeStatus.Active,
            OnboardingMethod = OnboardingMethod.Manual,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        seed.Users.Add(new ApplicationUser
        {
            Id = userId,
            TenantId = _tenantId,
            EmployeeId = employeeId,
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            NormalizedEmail = $"{first.ToUpperInvariant()}@ACME.COM",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    private static async Task TemplateAsync(
        ServiceProvider provider,
        string documentType,
        bool requiresSignature = false)
    {
        IApprovalTemplateService templates = provider.CreateScope().ServiceProvider
            .GetRequiredService<IApprovalTemplateService>();

        ApprovalTemplateResult created = await templates.CreateAsync(new ApprovalTemplateRequest
        {
            DocumentType = documentType,
            Name = $"Standard {documentType}",
            IsDefault = true,
            Steps =
            [
                new ApprovalTemplateStepRequest
                {
                    Label = "Line manager approval",
                    Approver = ApproverResolver.LineManager,
                    Required = true,
                    RequiresSignature = requiresSignature,
                },
            ],
        });

        Assert.True(created.Succeeded);
    }

    // A signature image, uploaded the way a client would. Real PNG magic bytes because
    // the files module sniffs the content against the declared type — a renamed
    // spreadsheet is refused there, which is what makes the purpose a usable check here.
    private static async Task<Guid> SignatureAsync(ServiceProvider provider)
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];

        FileUploadResult uploaded = await provider.CreateScope().ServiceProvider
            .GetRequiredService<IFileUploadService>()
            .UploadAsync(new FileUpload
            {
                Purpose = FilePurpose.ApprovalSignature,
                FileName = "signature.png",
                ContentType = "image/png",
                SizeBytes = png.Length,
                Content = new MemoryStream(png),
            });

        Assert.True(uploaded.Succeeded, uploaded.Rejection.ToString());

        return uploaded.File!.Id;
    }

    // Somebody in the tenant with no part in any chain: not the submitter, not a line
    // manager, not a department head, holding no access level.
    private async Task<Guid> OutsiderAsync()
    {
        Guid employeeId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Add(seed, employeeId, userId, "Dele");

        await seed.SaveChangesAsync();

        // Kept so a test can act as them with their own Self scope, which is what an
        // ordinary employee actually holds.
        _outsiderId = employeeId;

        return userId;
    }

    private static IApprovalService As(ServiceProvider provider, Guid userId)
    {
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();

        return provider.CreateScope().ServiceProvider.GetRequiredService<IApprovalService>();
    }

    private Task<ApprovalResult> StartAsync(
        ServiceProvider provider,
        Guid byUserId,
        string documentType = ApprovalDocumentTypes.Onboarding,
        string subjectType = "OnboardingRecord") =>
        As(provider, byUserId).StartAsync(new StartApprovalRequest
        {
            DocumentType = documentType,
            SubjectType = subjectType,
            SubjectId = Guid.NewGuid(),
            SubjectEmployeeId = _subjectId,
        });

    // ── the register ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheRegisterListsEveryApprovalWhoeverRaisedIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await StartAsync(provider, _managerUserId)).Succeeded);
        Assert.True((await StartAsync(provider, _headUserId)).Succeeded);

        // Asked as Cara, who raised one of the two. A queue would show her nothing here —
        // neither is waiting on her, both are with Ada.
        PagedResult<ApprovalDto> register =
            await As(provider, _headUserId).ListAsync(new ApprovalQuery());

        Assert.Equal(2, register.TotalItems);
        Assert.Equal(2, register.Items.Count);
    }

    [SkippableFact]
    public async Task TheRegisterSaysWhoRaisedEachOne()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await StartAsync(provider, _headUserId)).Succeeded);

        ApprovalDto only = Assert.Single(
            (await As(provider, _managerUserId).ListAsync(new ApprovalQuery())).Items);

        // Without this the register has a row per submission and no column for the
        // person who made it, which is the one thing it exists to show.
        Assert.Equal(_headUserId, only.SubmittedByUserId);
        Assert.Equal(_headId, only.SubmittedByEmployeeId);
        Assert.Equal("Cara Okafor", only.SubmittedByName);
    }

    // ── what the request is ─────────────────────────────────────────────────
    //
    // The register had a row per submission and no column saying what it was for. The
    // engine cannot supply that itself: it stores a subject type and a subject id and
    // never interprets them — it does not know what an onboarding record is, and
    // should not have to, or every new module means editing the engine. So the module
    // that starts the chain supplies the words, and the engine carries them.

    [SkippableFact]
    public async Task TheModuleStartingAChainSuppliesItsTitleAndSummary()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalResult raised = await As(provider, _managerUserId).StartAsync(
            new StartApprovalRequest
            {
                DocumentType = ApprovalDocumentTypes.Onboarding,
                SubjectType = "OnboardingRecord",
                SubjectId = Guid.NewGuid(),
                SubjectEmployeeId = _subjectId,
                Title = "Onboarding review",
                Summary = "Ben Adeyemi · started 1 Sept",
            },
            cancellationToken: default);

        Assert.True(raised.Succeeded, $"{raised.Outcome} / {raised.Reason}");

        Assert.Equal("Onboarding review", raised.Approval!.Title);
        Assert.Equal("Ben Adeyemi · started 1 Sept", raised.Approval.Summary);

        // On a fresh read too, not only in the answer to the start.
        ApprovalDto reread = (await As(provider, _managerUserId)
            .GetAsync(raised.Approval.Id, Viewer.Everything))!;

        Assert.Equal("Onboarding review", reread.Title);
    }

    // Null rather than a stand-in. The client already falls back to humanising the
    // document type, and a server-side guess would be a second vocabulary competing
    // with theirs — one that reads fine until a module supplies a real title.
    [SkippableFact]
    public async Task AChainStartedWithoutThemHasNoTitle()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto approval = (await StartAsync(provider, _managerUserId)).Approval!;

        Assert.Null(approval.Title);
        Assert.Null(approval.Summary);
    }

    [SkippableFact]
    public async Task TheRegisterCarriesTheTitleOnEveryRow()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await As(provider, _managerUserId).StartAsync(
            new StartApprovalRequest
            {
                DocumentType = ApprovalDocumentTypes.LeaveRequest,
                SubjectType = "LeaveRequest",
                SubjectId = Guid.NewGuid(),
                SubjectEmployeeId = _subjectId,
                Title = "Annual leave",
                Summary = "3–7 Mar · 5 days",
            },
            cancellationToken: default)).Succeeded);

        PagedResult<ApprovalDto> register =
            await As(provider, _managerUserId).ListAsync(new ApprovalQuery());

        ApprovalDto row = Assert.Single(register.Items);

        // The one thing the register exists to show, beside who raised it.
        Assert.Equal("Annual leave", row.Title);
        Assert.Equal("3–7 Mar · 5 days", row.Summary);
    }

    [SkippableFact]
    public async Task TheRegisterIsNewestFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto older = (await StartAsync(provider, _managerUserId)).Approval!;

        _clock.Advance(TimeSpan.FromHours(2));

        ApprovalDto newer = (await StartAsync(provider, _managerUserId)).Approval!;

        PagedResult<ApprovalDto> register =
            await As(provider, _managerUserId).ListAsync(new ApprovalQuery());

        // The opposite order to the queue. A queue is worked oldest first; a register is
        // read newest first, because the question is what just happened.
        Assert.Equal(newer.Id, register.Items[0].Id);
        Assert.Equal(older.Id, register.Items[1].Id);
    }

    [SkippableFact]
    public async Task TheRegisterFiltersByStatus()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto cancelled = (await StartAsync(provider, _managerUserId)).Approval!;
        Assert.True((await StartAsync(provider, _managerUserId)).Succeeded);

        Assert.True((await As(provider, _managerUserId)
            .CancelAsync(cancelled.Id, "Withdrawn")).Succeeded);

        PagedResult<ApprovalDto> live = await As(provider, _managerUserId)
            .ListAsync(new ApprovalQuery { Status = ApprovalStatus.InProgress });

        Assert.Single(live.Items);
        Assert.DoesNotContain(live.Items, item => item.Id == cancelled.Id);

        PagedResult<ApprovalDto> dead = await As(provider, _managerUserId)
            .ListAsync(new ApprovalQuery { Status = ApprovalStatus.Cancelled });

        Assert.Equal(cancelled.Id, Assert.Single(dead.Items).Id);
    }

    [SkippableFact]
    public async Task TheRegisterFiltersByDocumentType()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await StartAsync(provider, _managerUserId)).Succeeded);

        Assert.True((await StartAsync(
            provider,
            _managerUserId,
            ApprovalDocumentTypes.LeaveRequest,
            "LeaveRequest")).Succeeded);

        PagedResult<ApprovalDto> leave = await As(provider, _managerUserId).ListAsync(
            new ApprovalQuery { DocumentType = ApprovalDocumentTypes.LeaveRequest });

        Assert.Equal(
            ApprovalDocumentTypes.LeaveRequest,
            Assert.Single(leave.Items).DocumentType);
    }

    [SkippableFact]
    public async Task TheRegisterFiltersByWhoRaisedIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await StartAsync(provider, _managerUserId)).Succeeded);
        Assert.True((await StartAsync(provider, _headUserId)).Succeeded);

        // Named by employee rather than by user, because that is who the screen picks
        // from. The engine stores the account that submitted; this resolves across.
        PagedResult<ApprovalDto> carasOwn = await As(provider, _managerUserId)
            .ListAsync(new ApprovalQuery { RequesterEmployeeId = _headId });

        Assert.Equal(_headUserId, Assert.Single(carasOwn.Items).SubmittedByUserId);
    }

    [SkippableFact]
    public async Task TheRegisterFiltersBySubject()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await StartAsync(provider, _managerUserId)).Succeeded);

        PagedResult<ApprovalDto> aboutBen = await As(provider, _managerUserId)
            .ListAsync(new ApprovalQuery { SubjectEmployeeId = _subjectId });

        Assert.Single(aboutBen.Items);

        PagedResult<ApprovalDto> aboutCara = await As(provider, _managerUserId)
            .ListAsync(new ApprovalQuery { SubjectEmployeeId = _headId });

        Assert.Empty(aboutCara.Items);
    }

    [SkippableFact]
    public async Task TheRegisterFiltersByDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await StartAsync(provider, _managerUserId)).Succeeded);

        _clock.Advance(TimeSpan.FromDays(3));

        ApprovalDto later = (await StartAsync(provider, _managerUserId)).Approval!;

        // 3 September onwards: the second one only.
        PagedResult<ApprovalDto> recent = await As(provider, _managerUserId)
            .ListAsync(new ApprovalQuery { From = new DateOnly(2026, 9, 3) });

        Assert.Equal(later.Id, Assert.Single(recent.Items).Id);

        // The end of the range includes the whole of its day, rather than stopping at
        // midnight and dropping everything submitted during it.
        PagedResult<ApprovalDto> inclusive = await As(provider, _managerUserId)
            .ListAsync(new ApprovalQuery { To = new DateOnly(2026, 9, 1) });

        Assert.Single(inclusive.Items);
    }

    [SkippableFact]
    public async Task TheRegisterPages()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        for (int i = 0; i < 3; i++)
        {
            Assert.True((await StartAsync(provider, _managerUserId)).Succeeded);
            _clock.Advance(TimeSpan.FromMinutes(5));
        }

        PagedResult<ApprovalDto> first = await As(provider, _managerUserId)
            .ListAsync(new ApprovalQuery { Page = 1, PageSize = 2 });

        Assert.Equal(3, first.TotalItems);
        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasNextPage);

        PagedResult<ApprovalDto> second = await As(provider, _managerUserId)
            .ListAsync(new ApprovalQuery { Page = 2, PageSize = 2 });

        Assert.Single(second.Items);
        Assert.False(second.HasNextPage);
    }

    // ── comments ────────────────────────────────────────────────────────────
    //
    // A note without a decision. In the same history as the decisions, because "they
    // asked for a receipt, I sent one, they approved it" is one sequence — reading it
    // out of two tables interleaved by timestamp is how it gets told wrongly.

    [SkippableFact]
    public async Task TheSubmitterCanCommentOnTheirOwnRequest()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;

        ApprovalResult commented = await As(provider, _headUserId)
            .CommentAsync(raised.Id, "Receipt attached, sorry for the delay.");

        Assert.True(commented.Succeeded, $"{commented.Outcome} / {commented.Reason}");

        ApprovalEventDto note = Assert.Single(
            commented.Approval!.History,
            entry => entry.Type == "commented");

        Assert.Equal("Receipt attached, sorry for the delay.", note.Note);

        // Not a decision. The status and the round are untouched, or the history would
        // say something happened that did not.
        Assert.Equal(ApprovalStatus.InProgress, commented.Approval.Status);
        Assert.Equal(1, commented.Approval.Round);
    }

    [SkippableFact]
    public async Task AnApproverCanComment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;

        // Ada is the line manager, so the step is hers.
        Assert.True((await As(provider, _managerUserId)
            .CommentAsync(raised.Id, "Can you send the receipt?")).Succeeded);
    }

    // Somebody with no part in the chain. The right to comment comes from being party
    // to it, not from holding the module — which is the same reasoning the decide path
    // uses, and the reason this is not behind a permission attribute.
    [SkippableFact]
    public async Task SomebodyWithNoPartInTheChainCannotComment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;

        Guid outsiderUserId = await OutsiderAsync();

        ApprovalResult refused = await As(provider, outsiderUserId)
            .CommentAsync(raised.Id, "Nothing to do with me.");

        Assert.Equal(ApprovalOutcome.NotTheApprover, refused.Outcome);
    }

    // ── who may read one approval ───────────────────────────────────────────
    //
    // Three ways in, and the second is why this is not the employee-reach rule the
    // other registers use: a chain exists precisely so that somebody outside your team
    // can ask you to agree to something. Narrowing only by subject would hide a request
    // from the person being asked to decide it, and their queue would list items they
    // could not open.

    [SkippableFact]
    public async Task SomebodyWithNoPartInTheChainCannotReadIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;

        Guid outsiderUserId = await OutsiderAsync();

        // Their own scope, which reaches nobody but themselves — not the subject, and
        // they answer no step.
        Assert.Null(await As(provider, outsiderUserId)
            .GetAsync(raised.Id, Viewer.Themselves(_outsiderId)));
    }

    [SkippableFact]
    public async Task TheApproverCanReadWhatTheyAreAskedToDecide()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;

        // Ada is the line manager the step resolved to, and the subject is Cara —
        // outside a Self scope. Reaching it at all depends on being the approver.
        Assert.NotNull(await As(provider, _managerUserId)
            .GetAsync(raised.Id, Viewer.Themselves(_managerId)));
    }

    [SkippableFact]
    public async Task TheSubmitterCanReadTheirOwnRequest()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;

        Assert.NotNull(await As(provider, _headUserId)
            .GetAsync(raised.Id, Viewer.Themselves(_headId)));
    }

    [SkippableFact]
    public async Task AnEmptyCommentIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;

        Assert.False((await As(provider, _headUserId).CommentAsync(raised.Id, "   "))
            .Succeeded);
    }

    // Comments and decisions come back in one list, in the order they happened. The
    // exchange is the thing somebody reads, and splitting it would lose the order.
    [SkippableFact]
    public async Task CommentsAndDecisionsShareOneHistory()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;

        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await As(provider, _managerUserId)
            .CommentAsync(raised.Id, "Can you send the receipt?")).Succeeded);

        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await As(provider, _headUserId)
            .CommentAsync(raised.Id, "Sent.")).Succeeded);

        _clock.Advance(TimeSpan.FromMinutes(5));
        ApprovalResult decided = await As(provider, _managerUserId)
            .DecideAsync(raised.Id, ApprovalStepStatus.Approved);

        Assert.True(decided.Succeeded, $"{decided.Outcome} / {decided.Reason}");

        Assert.Equal(
            ["submitted", "commented", "commented", "approved"],
            decided.Approval!.History.Select(entry => entry.Type).ToArray());
    }

    // ── steps that have to be signed ────────────────────────────────────────

    // The requirement is the whole point of the feature, so the refusal is what is
    // worth pinning: without it, a chain configured to need a signature would accept an
    // unsigned approval and the record would be unable to say who put their name to it.
    [SkippableFact]
    public async Task ASignatureStepCannotBeApprovedWithoutOne()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(requiresSignature: true);

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;

        Assert.True(Assert.Single(raised.Steps).RequiresSignature);

        ApprovalResult refused = await As(provider, _managerUserId)
            .DecideAsync(raised.Id, ApprovalStepStatus.Approved);

        Assert.Equal(ApprovalOutcome.SignatureRequired, refused.Outcome);
    }

    [SkippableFact]
    public async Task ASignatureStepIsApprovedWithOne()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(requiresSignature: true);

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;
        Guid signature = await SignatureAsync(provider);

        ApprovalResult decided = await As(provider, _managerUserId)
            .DecideAsync(raised.Id, ApprovalStepStatus.Approved, signatureFileId: signature);

        Assert.True(decided.Succeeded, $"{decided.Outcome} / {decided.Reason}");
        Assert.Equal(signature, Assert.Single(decided.Approval!.Steps).SignatureFileId);
    }

    // Refusing is not signing. A step that insisted on a signature to reject would be
    // a step nobody could turn down.
    [SkippableFact]
    public async Task ASignatureStepCanStillBeRejectedWithoutOne()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(requiresSignature: true);

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;

        ApprovalResult decided = await As(provider, _managerUserId)
            .DecideAsync(raised.Id, ApprovalStepStatus.Rejected);

        Assert.True(decided.Succeeded, $"{decided.Outcome} / {decided.Reason}");
    }

    // The default, so existing chains are unaffected by the field arriving.
    [SkippableFact]
    public async Task AnOrdinaryStepNeedsNoSignature()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;

        Assert.False(Assert.Single(raised.Steps).RequiresSignature);

        Assert.True((await As(provider, _managerUserId)
            .DecideAsync(raised.Id, ApprovalStepStatus.Approved)).Succeeded);
    }

    // A timeline names people, not mailboxes.
    //
    // The actor's name is resolved on read from the user id the event already stored,
    // rather than written at the time. That is what makes it right for the entries
    // already in the database — the field used to be filled with the signed-in user's
    // email address, so every exchange recorded so far reads as "ben@acme.com said".
    [SkippableFact]
    public async Task TheHistoryNamesTheActorRatherThanTheirEmailAddress()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;

        Assert.True((await As(provider, _managerUserId)
            .CommentAsync(raised.Id, "Can you send the receipt?")).Succeeded);

        ApprovalResult decided = await As(provider, _managerUserId)
            .DecideAsync(raised.Id, ApprovalStepStatus.Approved);

        Assert.True(decided.Succeeded, $"{decided.Outcome} / {decided.Reason}");

        string[] actors = [.. decided.Approval!.History.Select(entry => entry.ActorName ?? "")];

        Assert.All(actors, actor =>
        {
            Assert.DoesNotContain("@", actor, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(actor));
        });

        // Cara raised it, Ada commented and approved. Both are named, so the entries
        // are attributed to the right person rather than merely to a non-email string.
        Assert.Equal(["Cara Okafor", "Ada Okafor", "Ada Okafor"], actors);
    }

    // ── intake ──────────────────────────────────────────────────────────────
    //
    // Raising a request of a type with no module of its own. The six built-in types
    // nothing else starts were decorative — a tenant could save a chain against
    // "asset_request" and nothing would ever start one. For these the approval is the
    // record, which is why intake can serve them and cannot serve the other five.

    [SkippableFact]
    public async Task OnlyTypesWithAChainAndNoModuleAreOffered()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        // Arrange configures default chains for onboarding and leave_request. Both are
        // module-driven, so neither may be raised here.
        Assert.Empty(await As(provider, _managerUserId).DocumentTypesAsync());

        await TemplateAsync(provider, ApprovalDocumentTypes.AssetRequest);

        RaisableTypeDto offered = Assert.Single(
            await As(provider, _managerUserId).DocumentTypesAsync());

        Assert.Equal(ApprovalDocumentTypes.AssetRequest, offered.Key);

        // Humanised from the key, so a tenant's own category reads from its own words
        // rather than a lookup table that would not have it.
        Assert.Equal("Asset request", offered.Label);

        // The fields the form draws, served rather than kept as a client constant.
        Assert.Equal(
            ["assetType", "model", "justification"],
            offered.Fields.Select(field => field.Key).ToArray());
    }

    [SkippableFact]
    public async Task ARaisedRequestCarriesItsFields()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await TemplateAsync(provider, ApprovalDocumentTypes.AssetRequest);

        ApprovalResult raised = await As(provider, _headUserId).RaiseAsync(
            new RaiseApprovalRequest
            {
                DocumentType = ApprovalDocumentTypes.AssetRequest,
                Title = "Replacement laptop",
                Summary = "Current machine is five years old",
                Fields = new Dictionary<string, string?>
                {
                    ["assetType"] = "Laptop",
                    ["model"] = "MacBook Pro 14",
                    ["justification"] = "Battery no longer holds a charge.",
                },
            });

        Assert.True(raised.Succeeded, $"{raised.Outcome} / {raised.Reason}");

        ApprovalDto approval = raised.Approval!;

        Assert.Equal("Replacement laptop", approval.Title);
        Assert.Equal("Laptop", approval.Fields["assetType"]);
        Assert.Equal("Battery no longer holds a charge.", approval.Fields["justification"]);

        // The schema travels with the values, so the detail page can label and order
        // them without holding its own copy.
        Assert.Equal(3, approval.FieldSchema.Count);

        // Its own subject: the engine insists every instance has one, and for a request
        // with no module behind it the approval is the record.
        Assert.Equal(approval.Id, approval.SubjectId);
        Assert.Equal("approval_request", approval.SubjectType);

        // And it runs like any other chain — resolved, waiting on somebody.
        Assert.Equal(ApprovalStatus.InProgress, approval.Status);
        Assert.NotNull(approval.CurrentStep);
    }

    // The rule this endpoint exists to enforce. Raising leave here would make an
    // approval with nothing behind it: no days reserved, no balance moved, no notice
    // check — and the settlement observer would skip it in silence, because there is no
    // leave request to update. The frontend's own intake form currently offers these
    // types; this is the backend disagreeing on purpose.
    [SkippableFact]
    public async Task AModuleDrivenTypeCannotBeRaisedHere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalResult refused = await As(provider, _headUserId).RaiseAsync(
            new RaiseApprovalRequest
            {
                DocumentType = ApprovalDocumentTypes.LeaveRequest,
                Title = "A week off",
            });

        Assert.Equal(ApprovalOutcome.NotAllowed, refused.Outcome);

        // Points at the right screen rather than just refusing.
        Assert.Contains("its own screen", refused.Reason!, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ARequestWithNoTitleIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await TemplateAsync(provider, ApprovalDocumentTypes.AssetRequest);

        ApprovalResult refused = await As(provider, _headUserId).RaiseAsync(
            new RaiseApprovalRequest
            {
                DocumentType = ApprovalDocumentTypes.AssetRequest,
                Title = "   ",
            });

        Assert.False(refused.Succeeded);
    }

    [SkippableFact]
    public async Task AMissingRequiredFieldIsRefusedByName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await TemplateAsync(provider, ApprovalDocumentTypes.AssetRequest);

        ApprovalResult refused = await As(provider, _headUserId).RaiseAsync(
            new RaiseApprovalRequest
            {
                DocumentType = ApprovalDocumentTypes.AssetRequest,
                Title = "Replacement laptop",
                Fields = new Dictionary<string, string?> { ["assetType"] = "Laptop" },
            });

        // Names the box to fill, rather than "invalid request".
        Assert.Equal("Justification is needed.", refused.Reason);
    }

    [SkippableFact]
    public async Task AFieldNobodyAskedForIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await TemplateAsync(provider, ApprovalDocumentTypes.AssetRequest);

        ApprovalResult refused = await As(provider, _headUserId).RaiseAsync(
            new RaiseApprovalRequest
            {
                DocumentType = ApprovalDocumentTypes.AssetRequest,
                Title = "Replacement laptop",
                Fields = new Dictionary<string, string?>
                {
                    ["assetType"] = "Laptop",
                    ["justification"] = "Old.",
                    ["approvedBy"] = "me",
                },
            });

        Assert.Contains("does not ask for", refused.Reason!, StringComparison.Ordinal);
    }

    // A type nobody wrote a schema for is still worth raising: a title, a reason and an
    // attachment is a perfectly good generic request.
    [SkippableFact]
    public async Task ATenantsOwnTypeCanBeRaisedWithNoExtraFields()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await TemplateAsync(provider, "travel_authorisation");

        ApprovalResult raised = await As(provider, _headUserId).RaiseAsync(
            new RaiseApprovalRequest
            {
                DocumentType = "travel_authorisation",
                Title = "Site visit to Kano",
            });

        Assert.True(raised.Succeeded, $"{raised.Outcome} / {raised.Reason}");
        Assert.Empty(raised.Approval!.Fields);
        Assert.Empty(raised.Approval.FieldSchema);
    }

    // An intake request is about the person raising it, so a line-manager chain
    // resolves against the requester. Somebody with no manager recorded cannot raise
    // one — refused at submission with the reason, rather than left sitting in nobody's
    // queue, which is the engine's existing rule and the right one here too.
    [SkippableFact]
    public async Task ARequesterWithNoLineManagerIsRefusedWithTheReason()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await TemplateAsync(provider, ApprovalDocumentTypes.AssetRequest);

        // Ada is at the top: nobody is recorded as her manager.
        ApprovalResult refused = await As(provider, _managerUserId).RaiseAsync(
            new RaiseApprovalRequest
            {
                DocumentType = ApprovalDocumentTypes.AssetRequest,
                Title = "Replacement laptop",
                Fields = new Dictionary<string, string?>
                {
                    ["assetType"] = "Laptop",
                    ["justification"] = "Old.",
                },
            });

        Assert.Equal(ApprovalOutcome.Unstartable, refused.Outcome);
        Assert.Equal("No line manager is recorded for this employee.", refused.Reason);
    }

    [SkippableFact]
    public async Task ATypeWithNoChainCannotBeRaised()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalResult refused = await As(provider, _headUserId).RaiseAsync(
            new RaiseApprovalRequest
            {
                DocumentType = ApprovalDocumentTypes.AssetRequest,
                Title = "Replacement laptop",
                Fields = new Dictionary<string, string?>
                {
                    ["assetType"] = "Laptop",
                    ["justification"] = "Old.",
                },
            });

        // Nobody to send it to. Which is also why the type was not offered.
        Assert.Equal(ApprovalOutcome.TemplateNotFound, refused.Outcome);
    }

    // A raised request appears in the register like any other, which is the point: one
    // queue, whatever route the request came in by.
    [SkippableFact]
    public async Task ARaisedRequestIsInTheRegisterAndInMine()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await TemplateAsync(provider, ApprovalDocumentTypes.AssetRequest);

        Assert.True((await As(provider, _headUserId).RaiseAsync(
            new RaiseApprovalRequest
            {
                DocumentType = ApprovalDocumentTypes.AssetRequest,
                Title = "Replacement laptop",
                Fields = new Dictionary<string, string?>
                {
                    ["assetType"] = "Laptop",
                    ["justification"] = "Old.",
                },
            })).Succeeded);

        PagedResult<ApprovalDto> register =
            await As(provider, _managerUserId).ListAsync(new ApprovalQuery());

        Assert.Equal("Replacement laptop", Assert.Single(register.Items).Title);

        PagedResult<ApprovalDto> mine =
            await As(provider, _headUserId).MineAsync(new PagedQuery());

        Assert.Single(mine.Items);
    }

    // ── signatures ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnApproverCanSignTheirDecision()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;

        Guid signature = await SignatureAsync(provider);

        ApprovalResult decided = await As(provider, _managerUserId).DecideAsync(
            raised.Id, ApprovalStepStatus.Approved, "Agreed.", signature);

        Assert.True(decided.Succeeded, $"{decided.Outcome} / {decided.Reason}");

        ApprovalStepDto step = decided.Approval!.Steps[0];

        // On the step rather than the run, because that is what it attests: this
        // person decided this step.
        Assert.Equal(signature, step.SignatureFileId);
        Assert.NotNull(step.SignatureUrl);
    }

    // Refusing is not signed. A signature stored against a rejection would read as the
    // reviewer having endorsed what they turned down.
    [SkippableFact]
    public async Task ARejectionCannotCarryASignature()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;

        ApprovalResult refused = await As(provider, _managerUserId).DecideAsync(
            raised.Id, ApprovalStepStatus.Rejected, "No.", await SignatureAsync(provider));

        Assert.False(refused.Succeeded);
        Assert.Equal(
            "A signature belongs on an approval, not on a refusal.", refused.Reason);
    }

    // The purpose is the check. A file uploaded as something else cannot become a
    // signature by being named one here — which is what stops a chain being "signed"
    // with a spreadsheet.
    [SkippableFact]
    public async Task AFileThatIsNotASignatureIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;

        ApprovalResult refused = await As(provider, _managerUserId).DecideAsync(
            raised.Id, ApprovalStepStatus.Approved, null, Guid.NewGuid());

        Assert.Equal(ApprovalOutcome.AttachmentNotAllowed, refused.Outcome);
    }

    [SkippableFact]
    public async Task ADecisionWithoutASignatureStillWorks()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;

        ApprovalResult decided = await As(provider, _managerUserId)
            .DecideAsync(raised.Id, ApprovalStepStatus.Approved);

        Assert.True(decided.Succeeded, $"{decided.Outcome} / {decided.Reason}");
        Assert.Null(decided.Approval!.Steps[0].SignatureFileId);
    }

    // ── what I submitted ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task MineListsOnlyWhatISubmitted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await StartAsync(provider, _managerUserId)).Succeeded);
        ApprovalDto carasOwn = (await StartAsync(provider, _headUserId)).Approval!;

        PagedResult<ApprovalDto> mine =
            await As(provider, _headUserId).MineAsync(new PagedQuery());

        Assert.Equal(carasOwn.Id, Assert.Single(mine.Items).Id);
    }

    // The point of a submissions list as opposed to a queue: it does not stop mattering
    // once somebody has decided. "What happened to the leave I asked for" is the
    // question, and the answer is usually a decided approval.
    [SkippableFact]
    public async Task MineKeepsShowingApprovalsAlreadyDecided()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto raised = (await StartAsync(provider, _headUserId)).Approval!;

        // Ada is the line manager, so the one step is hers to decide.
        Assert.True((await As(provider, _managerUserId)
            .DecideAsync(raised.Id, ApprovalStepStatus.Approved)).Succeeded);

        PagedResult<ApprovalDto> mine =
            await As(provider, _headUserId).MineAsync(new PagedQuery());

        ApprovalDto only = Assert.Single(mine.Items);

        Assert.Equal(raised.Id, only.Id);
        Assert.Equal(ApprovalStatus.Approved, only.Status);
    }

    [SkippableFact]
    public async Task MineIsEmptyForSomebodyWhoHasRaisedNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await StartAsync(provider, _managerUserId)).Succeeded);

        PagedResult<ApprovalDto> mine =
            await As(provider, _headUserId).MineAsync(new PagedQuery());

        Assert.Empty(mine.Items);
        Assert.Equal(0, mine.TotalItems);
    }

    [SkippableFact]
    public async Task MineIsNewestFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ApprovalDto older = (await StartAsync(provider, _headUserId)).Approval!;

        _clock.Advance(TimeSpan.FromHours(2));

        ApprovalDto newer = (await StartAsync(provider, _headUserId)).Approval!;

        PagedResult<ApprovalDto> mine =
            await As(provider, _headUserId).MineAsync(new PagedQuery());

        Assert.Equal(newer.Id, mine.Items[0].Id);
        Assert.Equal(older.Id, mine.Items[1].Id);
    }
}
