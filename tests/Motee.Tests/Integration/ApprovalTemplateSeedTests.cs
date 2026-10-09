using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Approvals;
using Motee.Application.Auth;
using Motee.Domain.Approvals;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// What a company has on day one. A tenant that registers and immediately onboards
// somebody must not be stopped by there being no chain configured — and equally must not
// find templates for modules that do not exist.
[Collection(PostgresCollection.Name)]
public class ApprovalTemplateSeedTests(PostgresFixture fixture)
{
    private static RegisterTenantRequest Request() => new()
    {
        FirstName = "Ada",
        LastName = "Okafor",
        Email = "ada@acme.com",
        CompanyName = "Acme Corporation",
        Password = "correct-horse",
        CountryCode = "NG",
    };

    private async Task<(Guid TenantId, ServiceProvider Provider)> ArrangeAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(Request());
        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = registration.TenantId;

        return (registration.TenantId, provider);
    }

    private static IApprovalTemplateService Templates(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IApprovalTemplateService>();

    [SkippableFact]
    public async Task ANewCompanyCanStartAnOnboardingApprovalImmediately()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        ApprovalTemplateDto template = Assert.Single(
            await Templates(owned).ListAsync(ApprovalDocumentTypes.Onboarding));

        Assert.True(template.IsDefault);
        Assert.True(template.IsActive);
        Assert.Equal(2, template.Steps.Count);
        Assert.Equal(ApproverResolver.LineManager, template.Steps[0].Approver);
        Assert.Equal(ApproverResolver.DepartmentHead, template.Steps[1].Approver);
    }

    // The department head step is optional on purpose. A department with no head is
    // ordinary in a young company, and a required step there would leave every onboarding
    // stuck behind a person who does not exist.
    [SkippableFact]
    public async Task TheDepartmentHeadStepIsOptional()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        ApprovalTemplateDto template = Assert.Single(
            await Templates(owned).ListAsync(ApprovalDocumentTypes.Onboarding));

        Assert.True(template.Steps[0].Required);
        Assert.False(template.Steps[1].Required);
    }

    // A screen full of templates for expense claims and job requisitions — neither of
    // which the product does — teaches people the screen lies. A category gets a default
    // when the module that starts it exists.
    [SkippableFact]
    public async Task NothingIsSeededForModulesThatDoNotExist()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        IReadOnlyList<ApprovalTemplateDto> all = await Templates(owned).ListAsync();

        Assert.Equal([ApprovalDocumentTypes.Onboarding], all.Select(t => t.DocumentType).Distinct());
    }

    // Seeded as a starting point companies own, like the access levels beside it — not as
    // a protected system template they would have to copy before changing anything.
    [SkippableFact]
    public async Task TheSeededChainIsTheTenantsToEdit()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        ApprovalTemplateDto seeded = Assert.Single(
            await Templates(owned).ListAsync(ApprovalDocumentTypes.Onboarding));

        Assert.False(seeded.IsSystem);

        ApprovalTemplateResult edited = await Templates(owned).UpdateAsync(
            seeded.Id,
            new ApprovalTemplateRequest
            {
                DocumentType = ApprovalDocumentTypes.Onboarding,
                Name = "Our onboarding",
                IsDefault = true,
                Steps =
                [
                    new ApprovalTemplateStepRequest
                    {
                        Label = "Line manager approval",
                        Approver = ApproverResolver.LineManager,
                        Required = true,
                    },
                ],
            });

        Assert.True(edited.Succeeded);
        Assert.Equal("Our onboarding", edited.Template!.Name);
    }

    // Registration runs before any tenant is current, so the context has nothing to stamp
    // these from — the seeder sets it explicitly. Getting that wrong writes rows the
    // query filter can never return.
    [SkippableFact]
    public async Task TheSeededChainBelongsToTheTenantThatRegistered()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await using MoteeDbContext unfiltered = fixture.CreateContext();

        ApprovalTemplate template = Assert.Single(
            await unfiltered.ApprovalTemplates.IgnoreQueryFilters().ToListAsync());

        Assert.Equal(tenantId, template.TenantId);

        Assert.All(
            await unfiltered.ApprovalTemplateSteps.IgnoreQueryFilters().ToListAsync(),
            step => Assert.Equal(tenantId, step.TenantId));
    }
}
