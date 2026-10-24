using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Application.Performance;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Performance;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// Feedback, asked for and given.
//
// The thing worth pinning is the classification. Peer, upward and manager are
// worked out from the reporting line rather than taken from the client, because
// the label is what a 360 is read through — a report marking their own feedback
// "manager" would give it weight it has not earned, and nothing downstream would
// question it.
[Collection(PostgresCollection.Name)]
public class PerformanceFeedbackTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _meId;
    private Guid _managerId;
    private Guid _reportId;
    private Guid _peerId;

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

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        Guid departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        // A small reporting line: Cara manages Bola, who manages Dele.
        _managerId = await HireAsync(provider, departmentId, "Cara", "cara@acme.com");
        _meId = await HireAsync(provider, departmentId, "Bola", "bola@acme.com", _managerId);
        _reportId = await HireAsync(provider, departmentId, "Dele", "dele@acme.com", _meId);
        _peerId = await HireAsync(provider, departmentId, "Emeka", "emeka@acme.com", _managerId);

        await ActAsAsync(provider, _meId, "bola");

        return provider;
    }

    // ICurrentEmployee resolves through the real lookup, so acting as somebody
    // means an account linked to them rather than a stub that skips the thing
    // being exercised.
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
                FirstName = handle,
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

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    [SkippableFact]
    public async Task AskingAColleagueCreatesAnOpenRequest()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        FeedbackRequestResult asked = await Resolve<IPerformanceFeedbackService>(provider)
            .AskAsync(new AskForFeedbackRequest
            {
                ColleagueId = _peerId,
                Context = "We worked on the gateway together.",
            });

        Assert.True(asked.Succeeded, asked.Outcome.ToString());
        Assert.Equal(FeedbackRequestStatus.Pending, asked.Request!.Status);
        Assert.Equal("Emeka Okafor", asked.Request.AskedOfName);
    }

    // A second ask of the same person is a nudge, not a new question — and two in
    // their list look like two different ones.
    [SkippableFact]
    public async Task TheSameColleagueIsNotAskedTwiceAtOnce()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceFeedbackService feedback = Resolve<IPerformanceFeedbackService>(provider);

        await feedback.AskAsync(new AskForFeedbackRequest { ColleagueId = _peerId });

        Assert.Equal(
            FeedbackOutcome.AlreadyAsked,
            (await feedback.AskAsync(new AskForFeedbackRequest { ColleagueId = _peerId }))
                .Outcome);
    }

    [SkippableFact]
    public async Task NobodyAsksThemselves()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            FeedbackOutcome.CannotBeAboutYourself,
            (await Resolve<IPerformanceFeedbackService>(provider)
                .AskAsync(new AskForFeedbackRequest { ColleagueId = _meId })).Outcome);
    }

    // A self-assessment is its own field on a review, written knowing it is one.
    [SkippableFact]
    public async Task NobodyWritesFeedbackAboutThemselves()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            FeedbackOutcome.CannotBeAboutYourself,
            (await Resolve<IPerformanceFeedbackService>(provider)
                .GiveAsync(new GiveFeedbackRequest
                {
                    AboutEmployeeId = _meId,
                    Message = "I was excellent.",
                })).Outcome);
    }

    // Worked out from the reporting line, not the request. Bola's feedback about
    // Dele, who reports to Bola, is downward — expressed as the writer being the
    // subject's manager.
    [SkippableFact]
    public async Task FeedbackFromAManagerIsClassifiedAsManagerFeedback()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        FeedbackResult given = await Resolve<IPerformanceFeedbackService>(provider)
            .GiveAsync(new GiveFeedbackRequest
            {
                AboutEmployeeId = _reportId,
                Message = "Strong quarter.",
            });

        Assert.True(given.Succeeded, given.Outcome.ToString());
        Assert.Equal(FeedbackType.Manager, given.Feedback!.Type);
    }

    // And the other way: Dele writing about Bola is upward.
    [SkippableFact]
    public async Task FeedbackFromAReportIsClassifiedAsUpward()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ActAsAsync(provider, _reportId, "dele");

        FeedbackResult given = await Resolve<IPerformanceFeedbackService>(provider)
            .GiveAsync(new GiveFeedbackRequest
            {
                AboutEmployeeId = _meId,
                Message = "More context on priorities would help.",
            });

        Assert.Equal(FeedbackType.Upward, given.Feedback!.Type);
    }

    [SkippableFact]
    public async Task FeedbackBetweenColleaguesIsPeerFeedback()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        FeedbackResult given = await Resolve<IPerformanceFeedbackService>(provider)
            .GiveAsync(new GiveFeedbackRequest
            {
                AboutEmployeeId = _peerId,
                Message = "Good to work with.",
            });

        Assert.Equal(FeedbackType.Peer, given.Feedback!.Type);
    }

    [SkippableFact]
    public async Task AnsweringAnAskClosesIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceFeedbackService feedback = Resolve<IPerformanceFeedbackService>(provider);

        // Emeka asks Bola.
        await ActAsAsync(provider, _peerId, "emeka");

        Guid requestId = (await feedback.AskAsync(
            new AskForFeedbackRequest { ColleagueId = _meId })).Request!.Id;

        // Bola answers.
        await ActAsAsync(provider, _meId, "bola2");

        IPerformanceFeedbackService asBola = Resolve<IPerformanceFeedbackService>(provider);

        Assert.Single(await asBola.AwaitingMeAsync());

        FeedbackResult given = await asBola.GiveAsync(new GiveFeedbackRequest
        {
            AboutEmployeeId = _peerId,
            Message = "Clear communicator.",
            RequestId = requestId,
        });

        Assert.True(given.Succeeded, given.Outcome.ToString());
        Assert.Equal(requestId, given.Feedback!.RequestId);

        // Off their queue, now it is answered.
        Assert.Empty(await asBola.AwaitingMeAsync());
    }

    // A valid id is not permission to close somebody else's request by answering
    // it.
    [SkippableFact]
    public async Task SomebodyCannotAnswerARequestAddressedToAnotherPerson()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceFeedbackService feedback = Resolve<IPerformanceFeedbackService>(provider);

        // Bola asks Emeka; Bola then tries to answer it himself.
        Guid requestId = (await feedback.AskAsync(
            new AskForFeedbackRequest { ColleagueId = _peerId })).Request!.Id;

        Assert.Equal(
            FeedbackOutcome.NotFound,
            (await feedback.GiveAsync(new GiveFeedbackRequest
            {
                AboutEmployeeId = _peerId,
                Message = "Answering my own request.",
                RequestId = requestId,
            })).Outcome);
    }

    // Saying no beats leaving it to rot in somebody's list.
    [SkippableFact]
    public async Task AColleagueCanDeclineAnAsk()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ActAsAsync(provider, _peerId, "emeka");

        Guid requestId = (await Resolve<IPerformanceFeedbackService>(provider)
            .AskAsync(new AskForFeedbackRequest { ColleagueId = _meId })).Request!.Id;

        await ActAsAsync(provider, _meId, "bola3");

        IPerformanceFeedbackService asBola = Resolve<IPerformanceFeedbackService>(provider);

        Assert.Equal(
            FeedbackRequestStatus.Declined,
            (await asBola.DeclineAsync(requestId)).Request!.Status);
        Assert.Empty(await asBola.AwaitingMeAsync());
        Assert.Equal(
            FeedbackOutcome.AlreadyResolved, (await asBola.DeclineAsync(requestId)).Outcome);
    }

    [SkippableFact]
    public async Task FeedbackNeedsSomethingWritten()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            FeedbackOutcome.MessageRequired,
            (await Resolve<IPerformanceFeedbackService>(provider)
                .GiveAsync(new GiveFeedbackRequest
                {
                    AboutEmployeeId = _peerId,
                    Message = "   ",
                })).Outcome);
    }

    [SkippableFact]
    public async Task SomebodyReadsWhatWasWrittenAboutThem()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await Resolve<IPerformanceFeedbackService>(provider).GiveAsync(new GiveFeedbackRequest
        {
            AboutEmployeeId = _peerId,
            Message = "Good to work with.",
        });

        await ActAsAsync(provider, _peerId, "emeka2");

        PerformanceFeedbackDto received = Assert.Single(
            await Resolve<IPerformanceFeedbackService>(provider).AboutMeAsync());

        Assert.Equal("Good to work with.", received.Message);
        Assert.Equal("Bola Okafor", received.FromName);
    }
}
