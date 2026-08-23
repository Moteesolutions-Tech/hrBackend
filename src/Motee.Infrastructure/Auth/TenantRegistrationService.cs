using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Motee.Application.Auth;
using Motee.Application.Tenancy;
using Motee.Domain.Common;
using Motee.Domain.Authorization;
using Motee.Infrastructure.Authorization;
using Motee.Domain.Identity;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Auth;

internal sealed class TenantRegistrationService(
    MoteeDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    AccessLevelSeeder accessLevels,
    TimeProvider timeProvider,
    ITenantSlugGenerator slugGenerator) : ITenantRegistrationService
{
    public async Task<RegisterTenantResult> RegisterAsync(
        RegisterTenantRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!CountryCode.TryParse(request.CountryCode, out CountryCode countryCode))
        {
            return RegisterTenantResult.Failed($"Unsupported country '{request.CountryCode}'.");
        }

        // Tenant and user commit together. A tenant left behind by a failed sign-up
        // would hold the unique slug and block every retry of that company name.
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            Tenant tenant = new()
            {
                Id = Guid.NewGuid(),
                Name = request.CompanyName.Trim(),
                Slug = await slugGenerator.GenerateAsync(request.CompanyName, cancellationToken),
                CountryCode = countryCode,
                Plan = "starter",
                Status = "trial",
                CreatedAt = DateTimeOffset.UtcNow,
                TrialEndsAt = DateTimeOffset.UtcNow.AddDays(30),
            };

            dbContext.Tenants.Add(tenant);
            await dbContext.SaveChangesAsync(cancellationToken);

            ApplicationUser user = new()
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                IsPlatformStaff = false,
                FirstName = request.FirstName.Trim(),
                MiddleName = string.IsNullOrWhiteSpace(request.MiddleName) ? null : request.MiddleName.Trim(),
                LastName = request.LastName.Trim(),
                Email = request.Email.Trim(),
                CreatedAt = DateTimeOffset.UtcNow,
                // Stays false until the OTP is verified.
                EmailConfirmed = false,
            };

            IdentityResult created = await userManager.CreateAsync(user, request.Password);

            if (!created.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);

                bool duplicate = created.Errors.Any(error => error.Code is "DuplicateEmail");

                return duplicate
                    ? RegisterTenantResult.Conflict(
                        "That email address is already registered. Sign in instead, or use another address.")
                    : RegisterTenantResult.Failed(
                        created.Errors.Select(error => error.Description).ToArray());
            }

            // The tenant's own access levels, and the founder holding the admin one.
            // Seeded inside the same transaction as the tenant: a company that exists
            // with no levels cannot create the level it would need to create levels.
            IReadOnlyList<AccessLevel> levels = accessLevels.Seed(tenant.Id);

            dbContext.UserAccessLevels.Add(new UserAccessLevel
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                UserId = user.Id,
                AccessLevelId = AccessLevelSeeder.FounderLevel(levels).Id,
                AssignedAt = timeProvider.GetUtcNow(),
            });

            // What actually guarantees they can never be locked out. The level above
            // is so they appear on the Access Levels screen holding something, rather
            // than as an unexplained exception.
            user.IsOwner = true;

            await dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new RegisterTenantResult
            {
                Errors = [],
                TenantId = tenant.Id,
                TenantSlug = tenant.Slug,
                UserId = user.Id,
                Email = user.Email,
                CountryCode = countryCode.Value,
            };
        }
        catch (DbUpdateException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            return RegisterTenantResult.Failed(RootMessage(exception));
        }
    }

    private static string RootMessage(Exception exception)
    {
        Exception current = exception;

        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current.Message;
    }
}
