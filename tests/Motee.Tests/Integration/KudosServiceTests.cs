using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Application.Recognition;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Recognition;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Kudos, out of the database.
//
// The half of these that matters is visibility: a private kudos must not leave
// the service for anybody who is not party to it, and that is enforced in the
// query rather than by a flag the client is trusted to honour. The rest is the
// derivations — reaction counts read from rows, the leaderboard, and the
// participation rate over the real headcount rather than the client's
// hardcoded 156.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class KudosServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(new DateOnly(2026, 4, 23).ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _engineeringId;
    private Guid _salesId;
    private Guid _adaId;
    private Guid _bolaId;
    private Guid _salesPersonId;

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

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        IDepartmentService departments = Resolve<IDepartmentService>(provider);

        _engineeringId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _salesId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "Sales", Code = "SAL" }))
            .Department!.Id;

        _adaId = await HireAsync(provider, "Ada", _engineeringId);
        _bolaId = await HireAsync(provider, "Bola", _engineeringId);
        _salesPersonId = await HireAsync(provider, "Chidi", _salesId);

        await AccountAsync(_adaId, "ada");
        await AccountAsync(_bolaId, "bola");
        await AccountAsync(_salesPersonId, "chidi");

        await ActAsync(provider, _adaId);

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IKudosService Kudos(ServiceProvider provider) =>
        Resolve<IKudosService>(provider);

    private async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        Guid departmentId) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;

    private async Task AccountAsync(Guid employeeId, string handle)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
        {
            Id = Guid.NewGuid(),
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

    private async Task ActAsync(ServiceProvider provider, Guid employeeId)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Guid userId = await seed.Users
            .IgnoreQueryFilters()
            .Where(user => user.EmployeeId == employeeId && user.TenantId == _tenantId)
            .Select(user => user.Id)
            .FirstAsync();

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();
    }

    private const string Message = "Carried the release on her own over a bank holiday.";

    private KudosRequest Sending(
        Guid? recipient = null,
        Guid? department = null,
        string message = Message,
        KudosType type = KudosType.Teamwork,
        CompanyValue value = CompanyValue.Collaboration,
        KudosVisibility visibility = KudosVisibility.Public,
        string? customTypeName = null) => new()
        {
            RecipientEmployeeId = recipient ?? (department is null ? _bolaId : null),
            RecipientDepartmentId = department,
            Message = message,
            KudosType = type,
            CustomTypeName = customTypeName,
            CompanyValue = value,
            Visibility = visibility,
        };

    private static async Task<KudosPostDto> SentAsync(
        ServiceProvider provider,
        KudosRequest request)
    {
        KudosResult result = await Kudos(provider).SendAsync(request);

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Post!;
    }

    // ── sending ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task SendsAKudosToAColleague()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KudosPostDto post = await SentAsync(provider, Sending());

        Assert.Equal(_adaId, post.SenderEmployeeId);
        Assert.Equal("Ada Okafor", post.SenderName);
        Assert.Equal("Bola Okafor", post.RecipientName);
        Assert.Equal("Engineering", post.SenderDepartmentName);

        // An individual kudos carries no recipient department: it is addressed
        // to the person, not to where they sit.
        Assert.Null(post.RecipientDepartmentId);
        Assert.Null(post.RecipientDepartmentName);
        Assert.Equal(KudosRecipientKind.Individual, post.RecipientKind);
        Assert.False(post.IsAnnounced);
        Assert.True(post.CanRemove);
    }

    [SkippableFact]
    public async Task SendsAKudosToAWholeDepartment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KudosPostDto post = await SentAsync(provider, Sending(department: _salesId));

        Assert.Equal(KudosRecipientKind.Department, post.RecipientKind);
        Assert.Equal("Sales", post.RecipientDepartmentName);
        Assert.Null(post.RecipientEmployeeId);
    }

    [SkippableFact]
    public async Task ABroadcastIsAnnouncedAndAPublicKudosIsNot()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await SentAsync(provider,
            Sending(visibility: KudosVisibility.Broadcast))).IsAnnounced);

        Assert.False((await SentAsync(provider,
            Sending(visibility: KudosVisibility.Public))).IsAnnounced);
    }

    // Nothing in the client stops this, and a wall where people congratulate
    // themselves is a wall nobody reads.
    [SkippableFact]
    public async Task RefusesCongratulatingYourself()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            KudosRefusal.CannotSendToSelf,
            (await Kudos(provider).SendAsync(Sending(recipient: _adaId))).Refusal);
    }

    [SkippableFact]
    public async Task RefusesAKudosAddressedToBothAPersonAndADepartment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KudosResult result = await Kudos(provider).SendAsync(new KudosRequest
        {
            RecipientEmployeeId = _bolaId,
            RecipientDepartmentId = _engineeringId,
            Message = Message,
        });

        Assert.Equal(KudosRefusal.RecipientAmbiguous, result.Refusal);
    }

    [SkippableFact]
    public async Task RefusesAMessageTooShortToBeRecognition()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            KudosRefusal.MessageRequired,
            (await Kudos(provider).SendAsync(Sending(message: "Well done"))).Refusal);
    }

    [SkippableFact]
    public async Task RefusesACustomTypeWithNoName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            KudosRefusal.CustomTypeNameRequired,
            (await Kudos(provider).SendAsync(Sending(type: KudosType.Custom))).Refusal);
    }

    [SkippableFact]
    public async Task RefusesAKudosForSomebodyWhoDoesNotWorkHere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            KudosOutcome.EmployeeNotFound,
            (await Kudos(provider).SendAsync(Sending(recipient: Guid.NewGuid()))).Outcome);
    }

    // ── who can see what ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task APrivateKudosNeverReachesAThirdParty()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KudosPostDto post = await SentAsync(
            provider, Sending(visibility: KudosVisibility.Private));

        // The recipient sees it.
        await ActAsync(provider, _bolaId);
        Assert.NotNull(await Kudos(provider).GetAsync(post.Id));

        // Somebody in another department does not.
        await ActAsync(provider, _salesPersonId);
        Assert.Null(await Kudos(provider).GetAsync(post.Id));
        Assert.Empty((await Kudos(provider).WallAsync(new KudosQuery())).Items);
    }

    // It was addressed to the department, so the department is who sees it.
    [SkippableFact]
    public async Task APrivateDepartmentKudosReachesThatDepartment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KudosPostDto post = await SentAsync(provider, Sending(
            department: _salesId, visibility: KudosVisibility.Private));

        await ActAsync(provider, _salesPersonId);
        Assert.NotNull(await Kudos(provider).GetAsync(post.Id));

        await ActAsync(provider, _bolaId);
        Assert.Null(await Kudos(provider).GetAsync(post.Id));
    }

    // Reacting to something you cannot see would confirm it exists.
    [SkippableFact]
    public async Task AThirdPartyCannotReactToAPrivateKudos()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KudosPostDto post = await SentAsync(
            provider, Sending(visibility: KudosVisibility.Private));

        await ActAsync(provider, _salesPersonId);

        Assert.Equal(
            KudosOutcome.NotFound,
            (await Kudos(provider).ToggleReactionAsync(post.Id, ReactionType.Clap)).Outcome);
    }

    // ── the wall ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task HoldsPinnedKudosAtTheTop()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KudosPostDto first = await SentAsync(provider, Sending(message: "First one in."));
        await SentAsync(provider, Sending(message: "Second one in, later."));

        Assert.True((await Kudos(provider).SetPinnedAsync(first.Id, true)).Succeeded);

        PagedResult<KudosPostDto> wall = await Kudos(provider).WallAsync(new KudosQuery());

        Assert.Equal(first.Id, wall.Items[0].Id);
        Assert.True(wall.Items[0].IsPinned);
    }

    [SkippableFact]
    public async Task FiltersTheWallByTypeAndValue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SentAsync(provider, Sending(
            message: "Shipped the migration tool.",
            type: KudosType.Innovation,
            value: CompanyValue.Innovation));

        await SentAsync(provider, Sending(type: KudosType.Teamwork));

        Assert.Single(
            (await Kudos(provider).WallAsync(
                new KudosQuery { KudosType = KudosType.Innovation })).Items);

        Assert.Single(
            (await Kudos(provider).WallAsync(
                new KudosQuery { CompanyValue = CompanyValue.Innovation })).Items);
    }

    [SkippableFact]
    public async Task FiltersTheWallToOneRecipient()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SentAsync(provider, Sending(recipient: _bolaId));
        await SentAsync(provider, Sending(recipient: _salesPersonId));

        PagedResult<KudosPostDto> wall = await Kudos(provider).WallAsync(
            new KudosQuery { RecipientEmployeeId = _salesPersonId });

        Assert.Equal(["Chidi Okafor"], wall.Items.Select(post => post.RecipientName));
    }

    [SkippableFact]
    public async Task FiltersTheWallToAMonth()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SentAsync(provider, Sending());

        Assert.Single((await Kudos(provider).WallAsync(new KudosQuery { Month = "2026-04" })).Items);
        Assert.Empty((await Kudos(provider).WallAsync(new KudosQuery { Month = "2026-03" })).Items);
    }

    [SkippableFact]
    public async Task DoesNotTreatASearchTermAsAWildcard()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SentAsync(provider, Sending(message: "Hit 100% of the quarter's target."));
        await SentAsync(provider, Sending(message: "Covered the on-call week at short notice."));

        PagedResult<KudosPostDto> wall = await Kudos(provider).WallAsync(
            new KudosQuery { Search = "100%" });

        Assert.Single(wall.Items);
    }

    // ── reactions ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ReactingTwiceTakesTheReactionBack()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KudosPostDto post = await SentAsync(provider, Sending());

        KudosResult on = await Kudos(provider).ToggleReactionAsync(post.Id, ReactionType.Clap);

        KudosReactionDto clap = on.Post!.Reactions.First(row => row.Type == ReactionType.Clap);

        Assert.Equal(1, clap.Count);
        Assert.True(clap.Mine);

        KudosResult off = await Kudos(provider).ToggleReactionAsync(post.Id, ReactionType.Clap);

        KudosReactionDto cleared =
            off.Post!.Reactions.First(row => row.Type == ReactionType.Clap);

        Assert.Equal(0, cleared.Count);
        Assert.False(cleared.Mine);
    }

    [SkippableFact]
    public async Task CountsEverybodysReactionsAndKnowsWhichAreMine()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KudosPostDto post = await SentAsync(provider, Sending());

        await Kudos(provider).ToggleReactionAsync(post.Id, ReactionType.Heart);

        await ActAsync(provider, _bolaId);
        await Kudos(provider).ToggleReactionAsync(post.Id, ReactionType.Heart);

        // Read as somebody who has not reacted.
        await ActAsync(provider, _salesPersonId);

        KudosReactionDto heart = (await Kudos(provider).GetAsync(post.Id))!
            .Reactions.First(row => row.Type == ReactionType.Heart);

        Assert.Equal(2, heart.Count);
        Assert.False(heart.Mine);
    }

    // Every type comes back, so the feed draws the whole row without having to
    // invent the zeroes.
    [SkippableFact]
    public async Task ReportsEveryReactionTypeIncludingTheUnusedOnes()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KudosPostDto post = await SentAsync(provider, Sending());

        Assert.Equal(
            Enum.GetValues<ReactionType>().Length,
            (await Kudos(provider).GetAsync(post.Id))!.Reactions.Count);
    }

    // ── comments ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task CommentsOnAKudos()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KudosPostDto post = await SentAsync(provider, Sending());

        KudosResult result = await Kudos(provider).CommentAsync(post.Id, "Seconded.");

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        KudosCommentDto comment = Assert.Single(result.Post!.Comments);

        Assert.Equal("Seconded.", comment.Message);
        Assert.Equal("Ada Okafor", comment.AuthorName);
        Assert.True(comment.CanRemove);
    }

    [SkippableFact]
    public async Task RefusesAnEmptyComment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KudosPostDto post = await SentAsync(provider, Sending());

        Assert.Equal(
            KudosRefusal.CommentRequired,
            (await Kudos(provider).CommentAsync(post.Id, "   ")).Refusal);
    }

    [SkippableFact]
    public async Task OnlyTheAuthorOrAModeratorCanRemoveAComment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KudosPostDto post = await SentAsync(provider, Sending());

        Guid commentId = (await Kudos(provider).CommentAsync(post.Id, "Seconded."))
            .Post!.Comments[0].Id;

        await ActAsync(provider, _bolaId);

        Assert.Equal(
            KudosOutcome.Forbidden,
            (await Kudos(provider).RemoveCommentAsync(
                post.Id, commentId, canRemoveAnybodys: false)).Outcome);

        Assert.True((await Kudos(provider).RemoveCommentAsync(
            post.Id, commentId, canRemoveAnybodys: true)).Succeeded);
    }

    // ── taking one down ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheSenderCanWithdrawTheirOwnKudos()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KudosPostDto post = await SentAsync(provider, Sending());

        Assert.True((await Kudos(provider)
            .RemoveAsync(post.Id, canRemoveAnybodys: false)).Succeeded);

        Assert.Null(await Kudos(provider).GetAsync(post.Id));
    }

    [SkippableFact]
    public async Task NobodyElseCanWithdrawItWithoutTheModerationGrant()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KudosPostDto post = await SentAsync(provider, Sending());

        await ActAsync(provider, _salesPersonId);

        Assert.Equal(
            KudosOutcome.Forbidden,
            (await Kudos(provider).RemoveAsync(post.Id, canRemoveAnybodys: false)).Outcome);

        Assert.True((await Kudos(provider)
            .RemoveAsync(post.Id, canRemoveAnybodys: true)).Succeeded);
    }

    [SkippableFact]
    public async Task RemovingAKudosTakesItsReactionsAndCommentsWithIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KudosPostDto post = await SentAsync(provider, Sending());

        await Kudos(provider).ToggleReactionAsync(post.Id, ReactionType.Clap);
        await Kudos(provider).CommentAsync(post.Id, "Seconded.");

        await Kudos(provider).RemoveAsync(post.Id, canRemoveAnybodys: false);

        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        Assert.Empty(await read.KudosReactions.ToListAsync());
        Assert.Empty(await read.KudosComments.ToListAsync());
    }

    // ── the figures ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ReportsTheWallsKpiCards()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SentAsync(provider, Sending(type: KudosType.Teamwork, value: CompanyValue.Integrity));
        await SentAsync(provider, Sending(type: KudosType.Teamwork, value: CompanyValue.Excellence));

        await ActAsync(provider, _bolaId);
        await SentAsync(provider, Sending(recipient: _adaId, type: KudosType.Growth,
            value: CompanyValue.Integrity));

        KudosTally tally = await Kudos(provider).SummaryAsync();

        Assert.Equal(3, tally.Total);
        Assert.Equal(3, tally.ThisMonth);
        Assert.Equal(KudosType.Teamwork, tally.CommonestType);
        Assert.Equal(CompanyValue.Integrity, tally.CommonestValue);

        // Two of the four people in post have given something — the owner has
        // no employee record, so the roster is Ada, Bola and Chidi.
        Assert.Equal(2, tally.Givers);
        Assert.Equal(67, tally.ParticipationPercent);
    }

    [SkippableFact]
    public async Task RanksTheLeaderboardByKudosReceived()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SentAsync(provider, Sending(recipient: _bolaId));
        await SentAsync(provider, Sending(recipient: _bolaId));
        await SentAsync(provider, Sending(recipient: _salesPersonId));

        IReadOnlyList<KudosStandingDto> board = await Kudos(provider).LeaderboardAsync();

        Assert.Equal("Bola Okafor", board[0].EmployeeName);
        Assert.Equal(2, board[0].Standing.Received);
        Assert.Equal(1, board[0].Standing.Rank);
        Assert.Equal("Engineering", board[0].DepartmentName);

        // The sender is on the board on nil received, with three given.
        Assert.Contains(
            board,
            entry => entry.EmployeeName == "Ada Okafor" && entry.Standing.Sent == 3);
    }

    [SkippableFact]
    public async Task ReportsWhatTheSignedInPersonHasReceivedAndGiven()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SentAsync(provider, Sending(recipient: _bolaId));

        await ActAsync(provider, _bolaId);
        await SentAsync(provider, Sending(recipient: _adaId));

        KudosForMeDto mine = await Kudos(provider).MineAsync();

        Assert.Equal(1, mine.ReceivedCount);
        Assert.Equal(1, mine.SentCount);
        Assert.Equal(1, mine.Streak);
    }

    // ── tenancy ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanyNeverSeesAnothersWall()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SentAsync(provider, Sending());

        RegisterTenantResult other = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ola",
            LastName = "Bello",
            Email = "ola@other.com",
            CompanyName = "Other Limited",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(other.Succeeded, string.Join("; ", other.Errors));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = other.TenantId;

        Assert.Empty((await Kudos(provider).WallAsync(new KudosQuery())).Items);
        Assert.Equal(0, (await Kudos(provider).SummaryAsync()).Total);
    }
}
