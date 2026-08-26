using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Motee.Application.Auth;
using Motee.Application.Notifications;
using Motee.Application.Tenancy;
using Motee.Domain.Common;
using Motee.Domain.Authorization;
using Motee.Infrastructure.Authorization;
using Motee.Domain.Identity;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Common;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Auth;

internal sealed class TenantRegistrationService(
    MoteeDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    AccessLevelSeeder accessLevels,
    TimeProvider timeProvider,
    IEmailDispatcher email,
    AppLinks links,
    IMemoryCache noticeCache,
    ITenantSlugGenerator slugGenerator) : ITenantRegistrationService
{
    // In memory on purpose. It bounds mail volume rather than enforcing a security
    // rule, so losing it on restart costs one extra notice, and a second instance
    // would double the ceiling rather than break anything. Persisting it would mean a
    // table written to by unauthenticated callers, which is a worse trade.
    private static readonly TimeSpan AlreadyExistsNoticeCooldown = TimeSpan.FromMinutes(15);

    private static string NoticeKey(Guid userId) => $"register-notice:{userId}";

    public async Task<RegisterTenantResult> RegisterAsync(
        RegisterTenantRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!CountryCode.TryParse(request.CountryCode, out CountryCode countryCode))
        {
            return RegisterTenantResult.Failed($"Unsupported country '{request.CountryCode}'.");
        }

        string emailAddress = request.Email.Trim();

        // Checked before anything is written, so an address that already has an account
        // produces no tenant, no slug and no wasted sequence values. The duplicate
        // branch further down stays as a backstop for two concurrent registrations of
        // the same address, which this check cannot see.
        ApplicationUser? existing = await userManager.FindByEmailAsync(emailAddress);

        if (existing is not null)
        {
            // Registering a new account hashes a password, which takes a few hundred
            // milliseconds. Skipping that here would make the duplicate path measurably
            // faster - and a stopwatch would then answer the question the identical
            // response refuses to. The result is deliberately discarded.
            _ = userManager.PasswordHasher.HashPassword(existing, request.Password);

            // At most one notice per address per window. Without it this endpoint is an
            // unauthenticated way to send mail to anyone who has an account, as often as
            // the caller likes — and the reply is identical either way, so the abuse is
            // invisible from the outside. A person who genuinely tried twice does not
            // need two copies of the same message.
            //
            // Suppressing the send cannot change the response: both paths return the
            // same result, and the send was queued rather than awaited, so skipping it
            // is not observable in timing either.
            if (noticeCache.TryGetValue(NoticeKey(existing.Id), out _))
            {
                return RegisterTenantResult.AlreadyRegisteredTo(emailAddress);
            }

            // Keyed on the user id rather than the submitted string, so casing and
            // whitespace variants cannot each earn their own send.
            noticeCache.Set(NoticeKey(existing.Id), true, AlreadyExistsNoticeCooldown);

            email.Send(existing.Email!, new AccountAlreadyExistsEmail
            {
                SignInUrl = links.SignIn,
                ForgotPasswordUrl = links.ForgotPassword,
            });

            return RegisterTenantResult.AlreadyRegisteredTo(emailAddress);
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
