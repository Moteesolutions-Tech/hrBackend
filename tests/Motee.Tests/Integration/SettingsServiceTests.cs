using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Leave;
using Motee.Application.Notifications;
using Motee.Application.Organisation;
using Motee.Application.Settings;
using Motee.Domain.Audit;
using Motee.Domain.Auth;
using Motee.Domain.Notifications;
using Motee.Domain.Settings;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Settings and configuration, out of the database.
//
// What these are really about is that the screen's controls now do something.
// The module switches close endpoints, the password policy is what the
// validator applies, the holiday year is the one the leave balances compute
// against, and the notification switches decide whether a row is written at
// all. Before this the panel had twenty controls, four endpoints, and the few
// that saved were saving where nothing looked.
//
// Written before the services.
[Collection(PostgresCollection.Name)]
public class SettingsServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero));

    private readonly RecordingEmailSender _mail = new();

    private Guid _tenantId;
    private Guid _ownerUserId;

    // The OTP is hashed, so a reset code cannot be read back out of the
    // database. Debug mode is off in the fixture on purpose — the codes come
    // from real Identity token generation — so the only place to read one is
    // the email it was sent in.
    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public string LastCode => Regex.Match(Sent[^1].TextBody, @"\b\d{6}\b").Value;
    }

    // ── modules ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ANewCompanyStartsOnTheDefaults()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        ModuleSettings modules = await Settings(provider).ModulesAsync();

        Assert.Equal(FeatureCatalogue.All.Count, modules.Modules.Count);
        Assert.True(Row(modules, "employee-management").Enabled);
        Assert.True(Row(modules, "leave-management").Enabled);
    }

    [SkippableFact]
    public async Task SwitchingAModuleOffIsRemembered()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Off(provider, "leave-management");

        Assert.False(Row(await Settings(provider).ModulesAsync(), "leave-management").Enabled);
    }

    // The screen's own behaviour, enforced here as well: a payload that says a
    // disabled feature is in the sidebar lands with it hidden, because the
    // sidebar must not offer a link to endpoints that refuse.
    [SkippableFact]
    public async Task ADisabledModuleIsHiddenFromTheSidebarWhateverThePayloadSaid()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SettingsResult<ModuleSettings> saved = await Settings(provider).SaveModulesAsync(
            new ModuleSettingsRequest
            {
                Modules = [new ModuleChoice
                {
                    Id = "learning",
                    Enabled = false,
                    ShowInSidebar = true,
                }],
            });

        Assert.True(saved.Succeeded);
        Assert.False(Row(saved.Value!, "learning").ShowInSidebar);
    }

    [SkippableFact]
    public async Task AModuleCanBeOnAndTuckedAway()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SettingsResult<ModuleSettings> saved = await Settings(provider).SaveModulesAsync(
            new ModuleSettingsRequest
            {
                Modules = [new ModuleChoice
                {
                    Id = "learning",
                    Enabled = true,
                    ShowInSidebar = false,
                }],
            });

        Assert.True(Row(saved.Value!, "learning").Enabled);
        Assert.False(Row(saved.Value!, "learning").ShowInSidebar);
    }

    // There is no product left without employee records, so the switch is
    // locked rather than one that fails on save.
    [SkippableFact]
    public async Task EmployeeRecordsCannotBeSwitchedOff()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SettingsResult<ModuleSettings> saved = await Off(provider, "employee-management");

        Assert.True(Row(saved.Value!, "employee-management").Enabled);
        Assert.True(Row(saved.Value!, "employee-management").Essential);
    }

    // Posting one switch must not switch off the ones it did not mention.
    [SkippableFact]
    public async Task SavingOneSwitchLeavesTheOthersAlone()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Off(provider, "leave-management");

        Assert.False(Row(await Settings(provider).ModulesAsync(), "leave-management").Enabled);
        Assert.True(Row(await Settings(provider).ModulesAsync(), "employee-management").Enabled);
    }

    [SkippableFact]
    public async Task AModuleThatIsNotInTheCatalogueIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SettingsResult<ModuleSettings> saved = await Settings(provider).SaveModulesAsync(
            new ModuleSettingsRequest
            {
                Modules = [new ModuleChoice { Id = "mod-nonsense", Enabled = true }],
            });

        Assert.Equal(SettingsRefusal.UnknownFeature, saved.Refusal);
    }

    // The wizard's spelling is the stored one, and the panel's is the other
    // half of the same disagreement. Either has to resolve to the same feature
    // or a company that enabled Learning in setup sees Training switched off.
    [SkippableFact]
    public async Task ThePanelsSpellingReachesTheWizardsFeature()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SettingsResult<ModuleSettings> saved = await Settings(provider).SaveModulesAsync(
            new ModuleSettingsRequest
            {
                Modules = [new ModuleChoice
                {
                    Id = "mod-training",
                    Enabled = true,
                    ShowInSidebar = true,
                }],
            });

        Assert.True(Row(saved.Value!, "learning").Enabled);
    }

    // A disabled module must stop answering for the owner too. The owner
    // bypasses permissions, and a feature switched off is not a permission.
    [SkippableFact]
    public async Task ADisabledModuleStopsAnsweringForEverybody()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Off(provider, "learning");

        Assert.False(await Resolve<IEnabledFeatures>(provider).AllowsAsync("talent.training"));
    }

    [SkippableFact]
    public async Task SettingsItselfKeepsAnsweringWhateverIsSwitchedOff()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        foreach (string id in FeatureCatalogue.Ids)
        {
            await Off(provider, id);
        }

        Assert.True(await Resolve<IEnabledFeatures>(provider).AllowsAsync("admin.settings"));
    }

    [SkippableFact]
    public async Task SwitchingAModuleOffIsAudited()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        // Leave, not Training: Training is off by default, and switching off
        // something already off is a save with no change — which is audited as
        // exactly that.
        await Off(provider, "leave-management");

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        List<AuditEntry> entries = await context.AuditEntries
            .Where(entry => entry.Module == "admin.settings")
            .ToListAsync();

        Assert.Contains(
            entries,
            entry => entry.Description.Contains("disabled Leave & Time-Off"));
    }

    // ── password policy ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ThePolicyStartsWhereTheSignUpFormPromises()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PasswordPolicyView view = await Settings(provider).PasswordPolicyAsync();

        Assert.Equal(PasswordPolicy.MinimumLength, view.Policy.MinLength);
        Assert.False(view.Policy.RequireSymbols);
    }

    [SkippableFact]
    public async Task ThePolicyIsSaved()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SavePolicyAsync(provider, minLength: 16, symbols: true);

        PasswordPolicyView after = await Settings(provider).PasswordPolicyAsync();

        Assert.Equal(16, after.Policy.MinLength);
        Assert.True(after.Policy.RequireSymbols);
    }

    // The screen's type carries requireSymbols and requireSpecialChars for one
    // rule and renders only the second. Either has to save or one of the two
    // halves of the frontend silently does nothing.
    [SkippableFact]
    public async Task EitherSpellingOfTheSymbolRuleSaves()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SettingsResult<PasswordPolicyView> saved = await Settings(provider)
            .SavePasswordPolicyAsync(new PasswordPolicyRequest
            {
                MinLength = 10,
                RequireSpecialChars = true,
            });

        Assert.True(saved.Value!.Policy.RequireSymbols);
    }

    [SkippableFact]
    public async Task APolicyOutsideTheSlidersIsBroughtInside()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SettingsResult<PasswordPolicyView> saved = await Settings(provider)
            .SavePasswordPolicyAsync(new PasswordPolicyRequest
            {
                MinLength = 500,
                SessionTimeoutMinutes = 1,
            });

        Assert.Equal(PasswordPolicy.MaximumLength, saved.Value!.Policy.MinLength);
        Assert.Equal(
            PasswordPolicy.MinimumSessionMinutes, saved.Value.Policy.SessionTimeoutMinutes);
    }

    // The point of the whole panel. Before this the requirements were fixed in
    // DependencyInjection for every tenant alike, so a company asking for
    // sixteen characters with symbols still had eight-character passwords
    // accepted.
    [SkippableFact]
    public async Task ThePolicyIsWhatAPasswordIsJudgedBy()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SavePolicyAsync(provider, minLength: 16, symbols: true);

        Assert.Equal(
            PasswordResetOutcome.WeakPassword,
            await ResetAsync(provider, "correct-horse-2"));
    }

    [SkippableFact]
    public async Task APasswordThatMeetsThePolicyIsAccepted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SavePolicyAsync(provider, minLength: 16, symbols: true);

        Assert.Equal(
            PasswordResetOutcome.Succeeded,
            await ResetAsync(provider, "a-long-enough-password!"));
    }

    // Reported distinctly, because the password may be perfectly strong and
    // "choose a stronger one" is advice somebody cannot act on.
    [SkippableFact]
    public async Task APasswordAlreadyInUseIsRefusedByItsOwnName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SavePolicyAsync(provider, preventReuse: 3);

        Assert.Equal(
            PasswordResetOutcome.PreviouslyUsed,
            await ResetAsync(provider, "correct-horse"));
    }

    [SkippableFact]
    public async Task APreviousPasswordCannotComeBack()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SavePolicyAsync(provider, preventReuse: 3);

        Assert.Equal(
            PasswordResetOutcome.Succeeded, await ResetAsync(provider, "second-password"));

        Assert.Equal(
            PasswordResetOutcome.PreviouslyUsed, await ResetAsync(provider, "correct-horse"));
    }

    // With the policy off nothing is kept. A table of everybody's old password
    // hashes that no rule reads is a liability with no purpose.
    [SkippableFact]
    public async Task WithNoReusePolicyNoHistoryIsKept()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            PasswordResetOutcome.Succeeded, await ResetAsync(provider, "second-password"));

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        Assert.Empty(await context.PasswordHistory.ToListAsync());
    }

    [SkippableFact]
    public async Task OnlyAsManyPasswordsAsThePolicyForbidsAreKept()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SavePolicyAsync(provider, preventReuse: 2);

        foreach (string password in (string[])["second-pass", "third-pass", "fourth-pass"])
        {
            Assert.Equal(PasswordResetOutcome.Succeeded, await ResetAsync(provider, password));
        }

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        // One kept entry plus the current password makes the two the policy
        // forbids. Keeping two entries as well would forbid three.
        Assert.Single(await context.PasswordHistory.ToListAsync());
    }

    // ── password expiry ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task APasswordInsideTheExpiryStillSignsIn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SavePolicyAsync(provider, expiryDays: 90);
        await ConfirmAsync();
        await StampAsync();

        _clock.Advance(TimeSpan.FromDays(80));

        Assert.Equal(LoginOutcome.Succeeded, await SignInAsync(provider, "correct-horse"));
    }

    [SkippableFact]
    public async Task ALapsedPasswordIsRefusedByItsOwnName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SavePolicyAsync(provider, expiryDays: 90);
        await ConfirmAsync();
        await StampAsync();

        _clock.Advance(TimeSpan.FromDays(100));

        Assert.Equal(LoginOutcome.PasswordExpired, await SignInAsync(provider, "correct-horse"));
    }

    // A lapsed password is still the wrong answer to the wrong password. Told
    // apart so somebody is not sent round the forgot-password flow for a typo.
    [SkippableFact]
    public async Task AWrongPasswordIsStillWrongWhenThePolicyHasLapsed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SavePolicyAsync(provider, expiryDays: 90);
        await ConfirmAsync();
        await StampAsync();

        _clock.Advance(TimeSpan.FromDays(100));

        Assert.Equal(
            LoginOutcome.InvalidCredentials, await SignInAsync(provider, "not-the-password"));
    }

    // The day a company first saves a 90-day expiry nobody has a recorded
    // change date. Expiring everybody at once is not what they asked for.
    [SkippableFact]
    public async Task AnAccountWithNoRecordedChangeDateNeverExpires()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SavePolicyAsync(provider, expiryDays: 1);
        await ConfirmAsync();

        await using (MoteeDbContext context = fixture.CreateContext())
        {
            await context.Users
                .Where(user => user.Id == _ownerUserId)
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(user => user.PasswordChangedAt, (DateTimeOffset?)null));
        }

        _clock.Advance(TimeSpan.FromDays(400));

        Assert.Equal(LoginOutcome.Succeeded, await SignInAsync(provider, "correct-horse"));
    }

    // ── session timeout ────────────────────────────────────────────────────

    // "Session timeout: 1 hour" is an idle timeout, and the refresh token is
    // where it becomes one: it rotates on every use, so an hour from issue is
    // an hour from last activity. Before this the dropdown changed nothing and
    // everybody had fourteen days.
    [SkippableFact]
    public async Task TheSessionTimeoutShortensTheRefreshWindow()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SavePolicyAsync(provider, sessionMinutes: 60);
        await ConfirmAsync();

        IssuedRefreshToken issued = await Resolve<IRefreshTokenService>(provider)
            .IssueAsync(_ownerUserId, "127.0.0.1");

        Assert.Equal(_clock.GetUtcNow().AddMinutes(60), issued.ExpiresAt);
    }

    [SkippableFact]
    public async Task ASessionOutlivingItsTimeoutStopsRefreshing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SavePolicyAsync(provider, sessionMinutes: 60);
        await ConfirmAsync();

        IssuedRefreshToken issued = await Resolve<IRefreshTokenService>(provider)
            .IssueAsync(_ownerUserId, "127.0.0.1");

        _clock.Advance(TimeSpan.FromMinutes(61));

        RefreshResult refreshed = await Resolve<IRefreshTokenService>(provider)
            .RotateAsync(issued.Value, "127.0.0.1");

        Assert.Equal(RefreshOutcome.Expired, refreshed.Outcome);
    }

    // ── holiday year ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheCyclesComeBackDerivedFromTheTurnover()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        HolidayYearSettings years = await Settings(provider).HolidayYearsAsync();

        Assert.True(years.CyclesAreDerived);
        Assert.Equal(1, years.StartMonth);
        Assert.Equal(new DateOnly(2026, 1, 1), Active(years).StartDate);
    }

    [SkippableFact]
    public async Task MovingTheTurnoverMovesTheCycles()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SettingsResult<HolidayYearSettings> saved = await Settings(provider)
            .SaveHolidayYearAsync(new HolidayYearRequest { StartMonth = 4, StartDay = 1 });

        Assert.True(saved.Succeeded);
        Assert.Equal(new DateOnly(2026, 4, 1), Active(saved.Value!).StartDate);
        Assert.Equal("Fiscal Year 2026/27", Active(saved.Value!).Label);
    }

    // Two answers to "which year is this" would file leave under one and show
    // the balance against the other. This is the whole reason the cycles are
    // not a list somebody types.
    [SkippableFact]
    public async Task TheActiveCycleIsTheOneLeaveComputesAgainst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Settings(provider).SaveHolidayYearAsync(
            new HolidayYearRequest { StartMonth = 4, StartDay = 1 });

        Guid employeeId = await HireAsync(provider);

        IReadOnlyList<LeaveBalanceDto> balances = await Resolve<ILeaveBalanceService>(provider)
            .ForEmployeeAsync(employeeId);

        HolidayYearCycle active = Active(await Settings(provider).HolidayYearsAsync());

        Assert.NotEmpty(balances);
        Assert.All(balances, balance => Assert.Equal(active.StartDate, balance.LeaveYearStart));
    }

    [SkippableFact]
    public async Task ATurnoverThatCannotExistIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SettingsResult<HolidayYearSettings> saved = await Settings(provider)
            .SaveHolidayYearAsync(new HolidayYearRequest { StartMonth = 2, StartDay = 31 });

        Assert.Equal(SettingsRefusal.TurnoverNotADate, saved.Refusal);
    }

    // ── subscription ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheSeatsInUseAreCounted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SubscriptionView before = await Settings(provider).SubscriptionAsync();

        await AccountAsync("ada");

        SubscriptionView after = await Settings(provider).SubscriptionAsync();

        Assert.Equal(before.UsedSeats + 1, after.UsedSeats);
    }

    // Nothing in this product stores a seat allowance, and a made-up ceiling is
    // worse than none — somebody plans a hiring round against it.
    [SkippableFact]
    public async Task ThereIsNoSeatCeilingToReport()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SubscriptionView subscription = await Settings(provider).SubscriptionAsync();

        Assert.Null(subscription.SeatLimit);
        Assert.False(subscription.InvoicesAvailable);
        Assert.Equal("Acme Corporation", subscription.CompanyName);
    }

    // ── pick lists ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheListsAreTheCatalogue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        IReadOnlyList<PickListView> lists = await Lists(provider).ListAsync();

        Assert.Equal(PickListCatalogue.All.Count, lists.Count);
    }

    // The real rows, so the screen shows what the employee form will offer. A
    // second stored list of departments would be one nothing reads.
    [SkippableFact]
    public async Task DepartmentsComeFromTheDepartmentsThemselves()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Resolve<IDepartmentService>(provider).CreateAsync(
            new DepartmentRequest { Name = "Engineering", Code = "ENG" });

        PickListView? list = await Lists(provider).FindAsync("department");

        Assert.NotNull(list);
        Assert.False(list.Editable);
        Assert.Equal("/organization/departments", list.OwnerRoute);
        Assert.Contains(list.Options, option => option.Value == "Engineering");
    }

    [SkippableFact]
    public async Task ADepartmentCannotBeAddedFromSettings()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PickListResult result = await Lists(provider).AddAsync(
            "department", new PickListValueRequest { Value = "Marketing" });

        Assert.Equal(PickListRefusal.ListNotConfigurable, result.Refusal);
    }

    [SkippableFact]
    public async Task AValueIsAddedToAConfigurableList()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PickListResult result = await Lists(provider).AddAsync(
            "asset-category", new PickListValueRequest { Value = "Laptop" });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");
        Assert.Single(result.List!.Options);
        Assert.True(result.List.Options[0].IsDefault);
    }

    [SkippableFact]
    public async Task TheSameValueInAnotherCaseIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Lists(provider).AddAsync("asset-category", new PickListValueRequest { Value = "Laptop" });

        PickListResult again = await Lists(provider).AddAsync(
            "asset-category", new PickListValueRequest { Value = "laptop" });

        Assert.Equal(PickListRefusal.DuplicateValue, again.Refusal);
    }

    [SkippableFact]
    public async Task AValueIsRenamed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await OptionAsync(provider, "Laptop");

        PickListResult renamed = await Lists(provider).UpdateAsync(
            "asset-category", id, new PickListValueRequest { Value = "Laptops" });

        Assert.True(renamed.Succeeded);
        Assert.Equal("Laptops", renamed.List!.Options[0].Value);
    }

    [SkippableFact]
    public async Task RemovingTheDefaultPromotesTheNextValue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid first = await OptionAsync(provider, "Laptop");
        await OptionAsync(provider, "Monitor");

        PickListResult removed = await Lists(provider).RemoveAsync("asset-category", first);

        Assert.True(removed.Succeeded);
        Assert.Single(removed.List!.Options);
        Assert.True(removed.List.Options[0].IsDefault);
    }

    [SkippableFact]
    public async Task TheDefaultMovesWhereItIsPut()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await OptionAsync(provider, "Laptop");
        Guid second = await OptionAsync(provider, "Monitor");

        PickListResult result = await Lists(provider).SetDefaultAsync("asset-category", second);

        Assert.Single(result.List!.Options, option => option.IsDefault);
        Assert.True(result.List.Options.Single(option => option.Id == second).IsDefault);
    }

    [SkippableFact]
    public async Task OneCompanysPickListsAreNotAnothers()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await OptionAsync(provider, "Laptop");

        RegisterTenantResult other = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Other",
            LastName = "Owner",
            Email = "owner@other.com",
            CompanyName = "Other Limited",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = other.TenantId;

        PickListView? list = await Lists(provider).FindAsync("asset-category");

        Assert.NotNull(list);
        Assert.Empty(list.Options);
    }

    // ── notification preferences ───────────────────────────────────────────

    [SkippableFact]
    public async Task SomebodyWhoHasNeverOpenedThePanelGetsTheDefaults()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        NotificationPreferenceView view = await Preferences(provider).MineAsync();

        Assert.Equal(NotificationGroups.All.Count, view.Groups.Count);
        Assert.All(view.Groups, group => Assert.True(group.InApp));
    }

    // Two of the three columns of switches had nothing behind them. The
    // contract says which, so the screen can say so rather than offering a
    // control that silently does nothing.
    [SkippableFact]
    public async Task TheChannelsSayWhichOnesDeliver()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        NotificationPreferenceView view = await Preferences(provider).MineAsync();

        Assert.Equal(
            ChannelAvailability.Available,
            view.Channels.Single(row => row.Channel == NotificationChannel.InApp).Availability);
        Assert.Equal(
            ChannelAvailability.Unavailable,
            view.Channels.Single(row => row.Channel == NotificationChannel.Push).Availability);
    }

    [SkippableFact]
    public async Task PreferencesAreSaved()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SilenceAsync(provider, "documents");

        NotificationPreferenceView after = await Preferences(provider).MineAsync();

        Assert.False(after.Groups.Single(group => group.Id == "documents").InApp);
        Assert.True(after.Groups.Single(group => group.Id == "performance").InApp);
    }

    [SkippableFact]
    public async Task AChannelThatCannotDeliverCannotBeSwitchedOn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SettingsResult<NotificationPreferenceView> saved = await Preferences(provider)
            .SaveMineAsync(new NotificationPreferenceRequest
            {
                Groups =
                [
                    new NotificationGroupChoice
                    {
                        Id = "documents",
                        InApp = true,
                        Email = true,
                        Push = true,
                    },
                ],
            });

        NotificationGroupRow row = saved.Value!.Groups.Single(group => group.Id == "documents");

        Assert.True(row.InApp);
        Assert.False(row.Email);
        Assert.False(row.Push);
    }

    [SkippableFact]
    public async Task ASettingThatIsNotATimeZoneIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SettingsResult<NotificationPreferenceView> saved = await Preferences(provider)
            .SaveMineAsync(new NotificationPreferenceRequest
            {
                Groups = [],
                TimeZoneId = "GMT+1",
            });

        Assert.Equal(SettingsRefusal.UnknownTimeZone, saved.Refusal);
    }

    // The dropdown offers Kiswahili and the type does not permit it. Either has
    // to save or the screen stores a value nothing accepts.
    [SkippableFact]
    public async Task ALanguageTheDropdownOffersIsAccepted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SettingsResult<NotificationPreferenceView> saved = await Preferences(provider)
            .SaveMineAsync(new NotificationPreferenceRequest
            {
                Groups = [],
                TimeZoneId = "Africa/Lagos",
                Language = "sw",
            });

        Assert.True(saved.Succeeded, $"{saved.Outcome} / {saved.Refusal}");
        Assert.Equal("sw", saved.Value!.Language);
    }

    // The point of the whole panel: the switches decide whether a row is
    // written. Without this it was ten rows of decoration and the bell told
    // everybody everything.
    [SkippableFact]
    public async Task SilencingAGroupStopsItsNotifications()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SilenceAsync(provider, "documents");

        bool raised = await Resolve<INotificationService>(provider).RaiseAsync(
            Raising(NotificationKind.ContractExpiring));

        Assert.False(raised);

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        Assert.Empty(await context.Notifications.ToListAsync());
    }

    [SkippableFact]
    public async Task SilencingOneGroupLeavesTheOthersHeard()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SilenceAsync(provider, "documents");

        Assert.True(await Resolve<INotificationService>(provider).RaiseAsync(
            Raising(NotificationKind.ReviewOverdue)));
    }

    [SkippableFact]
    public async Task SomebodyWithNoPreferencesStillHearsEverything()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True(await Resolve<INotificationService>(provider).RaiseAsync(
            Raising(NotificationKind.ContractExpiring)));
    }

    // ── sessions ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheDevicesAreTheLiveSessions()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ConfirmAsync();
        await SignInAsync(provider, "correct-horse");

        IReadOnlyList<DeviceSessionDto> sessions = await Security(provider).SessionsAsync();

        Assert.Single(sessions);

        // The fixture's agent is "Motee.Tests", which is not a browser — so it
        // reads as unknown rather than being guessed at. That is the honest
        // answer for a request with nothing recognisable in its User-Agent.
        Assert.Equal("Unknown device", sessions[0].Name);
        Assert.Equal(_clock.GetUtcNow(), sessions[0].SignedInAt);
    }

    // The thing a trusted-devices table of its own could not do: the device
    // actually stops working.
    [SkippableFact]
    public async Task RevokingADeviceSignsItOut()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        IssuedRefreshToken issued = await Resolve<IRefreshTokenService>(provider)
            .IssueAsync(_ownerUserId, "127.0.0.1");

        DeviceSessionDto device = (await Security(provider).SessionsAsync()).Single();

        Assert.Equal(
            SettingsOutcome.Succeeded, await Security(provider).RevokeSessionAsync(device.Id));

        RefreshResult refreshed = await Resolve<IRefreshTokenService>(provider)
            .RotateAsync(issued.Value, "127.0.0.1");

        Assert.Equal(RefreshOutcome.Revoked, refreshed.Outcome);
    }

    [SkippableFact]
    public async Task RevokingADeviceTwiceIsStillDone()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Resolve<IRefreshTokenService>(provider).IssueAsync(_ownerUserId, "127.0.0.1");

        DeviceSessionDto device = (await Security(provider).SessionsAsync()).Single();

        await Security(provider).RevokeSessionAsync(device.Id);

        Assert.Equal(
            SettingsOutcome.Succeeded, await Security(provider).RevokeSessionAsync(device.Id));
    }

    // A valid id is not permission to sign somebody else's device out.
    [SkippableFact]
    public async Task SomebodyElsesDeviceCannotBeRevoked()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid otherUserId = await AccountAsync("ada");

        await Resolve<IRefreshTokenService>(provider).IssueAsync(otherUserId, "127.0.0.1");

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        Guid tokenId = (await context.RefreshTokens.SingleAsync()).Id;

        Assert.Equal(
            SettingsOutcome.NotFound, await Security(provider).RevokeSessionAsync(tokenId));
    }

    [SkippableFact]
    public async Task AnExpiredSessionIsNotADevice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Resolve<IRefreshTokenService>(provider).IssueAsync(_ownerUserId, "127.0.0.1");

        _clock.Advance(TimeSpan.FromDays(15));

        Assert.Empty(await Security(provider).SessionsAsync());
    }

    // ── login activity ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ASignInShowsUpInTheActivity()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ConfirmAsync();
        await SignInAsync(provider, "correct-horse");

        IReadOnlyList<AccountEventDto> activity = await Security(provider).ActivityAsync();

        Assert.Contains(activity, entry => entry.Kind == AccountEventKind.SignIn && entry.Succeeded);
    }

    // The entry an auditor looks for first, and the reason this is read from
    // the audit trail rather than kept twice.
    [SkippableFact]
    public async Task AFailedSignInShowsUpInTheActivity()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ConfirmAsync();
        await SignInAsync(provider, "not-the-password");

        IReadOnlyList<AccountEventDto> activity = await Security(provider).ActivityAsync();

        Assert.Contains(
            activity,
            entry => entry.Kind == AccountEventKind.FailedSignIn && !entry.Succeeded);
    }

    // A guessed city on a security screen is the one thing worse than a blank
    // one: somebody decides a sign-in was not theirs on the strength of it.
    [SkippableFact]
    public async Task NoLocationIsGuessedFromAnAddress()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await ConfirmAsync();
        await SignInAsync(provider, "correct-horse");

        Assert.All(await Security(provider).ActivityAsync(), entry => Assert.Null(entry.Location));
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

        ServiceProvider provider = fixture.BuildProvider(services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            services.AddScoped<IEmailSender>(_ => _mail);
        });

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            _ownerUserId.ToString();

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static ISettingsService Settings(ServiceProvider provider) =>
        Resolve<ISettingsService>(provider);

    private static IPickListService Lists(ServiceProvider provider) =>
        Resolve<IPickListService>(provider);

    private static INotificationPreferenceService Preferences(ServiceProvider provider) =>
        Resolve<INotificationPreferenceService>(provider);

    private static ISecuritySettingsService Security(ServiceProvider provider) =>
        Resolve<ISecuritySettingsService>(provider);

    private static ModuleSettingRow Row(ModuleSettings settings, string id) =>
        settings.Modules.Single(module => module.Id == id);

    private static HolidayYearCycle Active(HolidayYearSettings years) =>
        years.Cycles.Single(cycle => cycle.IsActive);

    private static Task<SettingsResult<ModuleSettings>> Off(
        ServiceProvider provider,
        string id) =>
        Settings(provider).SaveModulesAsync(new ModuleSettingsRequest
        {
            Modules = [new ModuleChoice { Id = id, Enabled = false }],
        });

    private static Task<SettingsResult<PasswordPolicyView>> SavePolicyAsync(
        ServiceProvider provider,
        int minLength = PasswordPolicy.MinimumLength,
        bool symbols = false,
        int expiryDays = 0,
        int preventReuse = 0,
        int sessionMinutes = 60) =>
        Settings(provider).SavePasswordPolicyAsync(new PasswordPolicyRequest
        {
            MinLength = minLength,
            RequireSymbols = symbols,
            ExpiryDays = expiryDays,
            PreventReuse = preventReuse,
            SessionTimeoutMinutes = sessionMinutes,
        });

    private static async Task<Guid> OptionAsync(ServiceProvider provider, string value)
    {
        PickListResult result = await Lists(provider).AddAsync(
            "asset-category", new PickListValueRequest { Value = value });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.List!.Options.Single(option => option.Value == value).Id;
    }

    private static Task SilenceAsync(ServiceProvider provider, string group) =>
        Preferences(provider).SaveMineAsync(new NotificationPreferenceRequest
        {
            Groups = [new NotificationGroupChoice { Id = group, InApp = false }],
        });

    private RaiseNotificationRequest Raising(NotificationKind kind) => new()
    {
        TenantId = _tenantId,
        UserId = _ownerUserId,
        Kind = kind,
        Title = "Something needs your attention",
        Body = "Have a look when you get a moment.",
        SubjectId = Guid.NewGuid(),
    };

    // Registration leaves the account unconfirmed until the OTP is verified,
    // and sign-in refuses an unconfirmed account before it reaches the policy.
    private async Task ConfirmAsync()
    {
        await using MoteeDbContext context = fixture.CreateContext();

        await context.Users
            .Where(user => user.Id == _ownerUserId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.EmailConfirmed, true));
    }

    // Registration stamps PasswordChangedAt from the wall clock rather than the
    // injected one, and the fixture's RegisterAsync builds its own provider
    // anyway — so the owner's stamp is the real today, which is ahead of this
    // test's clock. Brought back to the test's own now, or advancing the clock
    // by a hundred days would still land before the password was set.
    private async Task StampAsync()
    {
        await using MoteeDbContext context = fixture.CreateContext();

        await context.Users
            .Where(user => user.Id == _ownerUserId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                user => user.PasswordChangedAt, _clock.GetUtcNow()));
    }

    private static async Task<LoginOutcome> SignInAsync(ServiceProvider provider, string password) =>
        (await Resolve<ILoginService>(provider).LoginAsync(new LoginRequest
        {
            Email = "owner@acme.com",
            Password = password,
            IpAddress = "127.0.0.1",
        })).Outcome;

    // Through the reset path, which is one of the three places a password is
    // set and the only one with a password to replace.
    private async Task<PasswordResetOutcome> ResetAsync(
        ServiceProvider provider,
        string password)
    {
        await Resolve<IPasswordResetService>(provider).RequestAsync("owner@acme.com");

        return await Resolve<IPasswordResetService>(provider)
            .ResetAsync("owner@acme.com", _mail.LastCode, password);
    }

    private async Task<Guid> HireAsync(ServiceProvider provider)
    {
        Guid departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        return (await Resolve<Application.Employees.IEmployeeService>(provider)
            .CreateAsync(new Application.Employees.EmployeeRequest
            {
                FirstName = "Ada",
                LastName = "Okafor",
                Email = "ada@acme.com",
                Phone = "08012345678",
                JobTitle = "Engineer",
                DepartmentId = departmentId,
                EmploymentType = Domain.Organisation.EmploymentType.FullTime,
                Status = Domain.Employees.EmployeeStatus.Active,
                StartDate = new DateOnly(2024, 1, 10),
            })).Employee!.Id;
    }

    private async Task<Guid> AccountAsync(string handle)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Guid userId = Guid.NewGuid();

        seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
        {
            Id = userId,
            TenantId = _tenantId,
            FirstName = handle,
            LastName = "Okafor",
            Email = $"{handle}@acme.com",
            UserName = $"{handle}@acme.com",
            NormalizedEmail = $"{handle.ToUpperInvariant()}@ACME.COM",
            EmailConfirmed = true,
            CreatedAt = _clock.GetUtcNow(),
        });

        await seed.SaveChangesAsync();

        return userId;
    }
}
