using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
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
    Approvals.ApprovalTemplateSeeder approvalTemplates,
    Leave.LeaveSeeder leave,
    Attendance.WorkScheduleSeeder workSchedules,
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

            // The tenant's own access levels, held by nobody yet. Seeded inside the same
            // transaction as the tenant: a company that exists with no levels cannot
            // create the level it would need to create levels.
            accessLevels.Seed(tenant.Id);

            // The approval chain the onboarding module starts. Seeded here so a company
            // that invites somebody on their first day is not stopped by there being no
            // template configured.
            approvalTemplates.Seed(tenant.Id);

            // Leave types, their policies, and three years of public holidays. Country
            // dependent, unlike the seeders above: statutory entitlement and the days a
            // country is closed differ, and a shared default would be wrong for one of
            // the two on the day it was written.
            leave.Seed(tenant.Id, countryCode);

            // The working week everybody is judged against until the company says
            // otherwise. Country dependent for the same reason leave is: the hours a
            // standard day runs differ, and one shared default would be wrong for one
            // of the two from the day it was written.
            workSchedules.Seed(tenant.Id, countryCode);

            // The owner, and deliberately nothing else.
            //
            // They used to be handed the HR Admin level too, so the Access Levels screen
            // would show them holding something rather than as an unexplained exception.
            // That made the registrant the HR admin by construction — and HR Admin is a
            // job, which a company gives to whoever does it. A new tenant now has nobody
            // holding it until the owner invites or onboards one, and "who are the HR
            // admins" has an honest answer from the first day, including "nobody yet".
            //
            // The owner is the exception, and showing them as one is correct: the bypass
            // is what guarantees a company can never lock itself out by misconfiguring
            // its own levels, and that is a property of the account rather than a role.
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
        catch (DbUpdateException exception) when (IsDuplicate(exception))
        {
            await transaction.RollbackAsync(cancellationToken);

            // The only database failure that is the caller's to fix, and only as a
            // backstop: the address is already checked above, so reaching here means two
            // registrations raced for the same one.
            //
            // A written message, never the exception's. Postgres names the constraint it
            // rejected, which is internal structure and tells the person nothing they can
            // act on.
            return RegisterTenantResult.Conflict(
                "An account already exists for that email address.");
        }
        catch (DbUpdateException)
        {
            // Anything else is ours, not theirs.
            //
            // This used to return the innermost exception message as a validation error,
            // which meant a missing column — a broken deployment — came back as a 400
            // reading "42703: column \"attachment_note\" of relation
            // \"approval_templates\" does not exist". Three things wrong with that: the
            // user saw our schema, the user was blamed for our fault, and because it was
            // a handled 400 rather than an unhandled 500 it never reached the exception
            // handler, so nothing alerted. Every registration on that environment was
            // failing and the system's own view was that users kept sending bad input.
            //
            // Rolled back and rethrown. The caller gets the generic 500, the detail goes
            // to the log, and Slack hears about it.
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // Unique violation, which is the one constraint this path legitimately races on.
    // Matched on the SQLSTATE rather than on message text, which is localised and
    // reworded between server versions.
    internal static bool IsDuplicate(DbUpdateException exception) =>
        exception.GetBaseException() is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
