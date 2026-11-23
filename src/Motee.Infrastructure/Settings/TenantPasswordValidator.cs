using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Motee.Domain.Auth;
using Motee.Domain.Settings;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Settings;

// Applies the company's own password policy.
//
// Identity's options are configured once per process, so the requirements were
// the same for every tenant: eight characters and nothing else. A company that
// set a sixteen-character minimum with symbols still had eight-character
// passwords accepted, and nothing on the screen said so.
//
// A validator rather than a check at each call site, because there are three
// places a password is set — registration, accepting an invitation, and a
// reset — and all three already run every registered IPasswordValidator. One of
// them forgetting is how a policy ends up applying to two paths out of three.
internal sealed class TenantPasswordValidator(MoteeDbContext dbContext)
    : IPasswordValidator<ApplicationUser>
{
    // Prefixed "Password" to match Identity's own codes, because callers key
    // off that prefix to tell a rejected password from a rejected account —
    // accepting an invitation reports WeakPassword on anything starting
    // "Password" and AccountExists on everything else, so a code of our own
    // spelling would have landed as "that account already exists".
    public const string TooShortCode = "PasswordPolicyTooShort";
    public const string NeedsUppercaseCode = "PasswordPolicyNeedsUppercase";
    public const string NeedsNumberCode = "PasswordPolicyNeedsNumber";
    public const string NeedsSymbolCode = "PasswordPolicyNeedsSymbol";
    public const string PreviouslyUsedCode = "PasswordPolicyPreviouslyUsed";

    public async Task<IdentityResult> ValidateAsync(
        UserManager<ApplicationUser> manager,
        ApplicationUser user,
        string? password)
    {
        PasswordPolicy policy = await PolicyAsync(user);

        PasswordRefusal refusal = PasswordRules.Check(policy, password);

        if (refusal != PasswordRefusal.None)
        {
            return IdentityResult.Failed(Describe(refusal, policy));
        }

        if (password is not null && await IsReuseAsync(manager, user, password, policy))
        {
            return IdentityResult.Failed(Describe(PasswordRefusal.PreviouslyUsed, policy));
        }

        return IdentityResult.Success;
    }

    // The policy of the tenant the account belongs to.
    //
    // The default where there is none, which is the case during registration:
    // the company does not exist yet, so the only honest requirement is the one
    // the sign-up form promises.
    private async Task<PasswordPolicy> PolicyAsync(ApplicationUser user)
    {
        if (user.TenantId is not Guid tenantId)
        {
            return PasswordPolicy.Default;
        }

        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync();

        return settings?.PasswordPolicy ?? PasswordPolicy.Default;
    }

    // Against the current password and the kept history, newest first.
    //
    // Verified rather than compared: what is stored is a hash with its own salt,
    // so the same password hashes differently every time and the only way to
    // ask is to verify.
    private async Task<bool> IsReuseAsync(
        UserManager<ApplicationUser> manager,
        ApplicationUser user,
        string password,
        PasswordPolicy policy)
    {
        int depth = PasswordRules.HistoryDepth(policy);

        if (depth == 0)
        {
            return false;
        }

        // The current one counts. "Prevent reuse of the last five" that lets
        // somebody re-set the password they already have is not preventing
        // anything.
        if (user.PasswordHash is string current && Matches(manager, user, current, password))
        {
            return true;
        }

        if (user.Id == Guid.Empty)
        {
            // A brand new account. Nothing to have reused, and querying by an
            // unassigned id would match whatever row has it.
            return false;
        }

        List<string> previous = await dbContext.PasswordHistory
            .AsNoTracking()
            .Where(entry => entry.UserId == user.Id)
            .OrderByDescending(entry => entry.CreatedAt)
            .Take(depth)
            .Select(entry => entry.PasswordHash)
            .ToListAsync();

        return previous.Any(hash => Matches(manager, user, hash, password));
    }

    private static bool Matches(
        UserManager<ApplicationUser> manager,
        ApplicationUser user,
        string hash,
        string password) =>
        manager.PasswordHasher.VerifyHashedPassword(user, hash, password)
            is PasswordVerificationResult.Success
            or PasswordVerificationResult.SuccessRehashNeeded;

    // Sentences rather than Identity's own codes, because these reach the
    // sign-up and reset forms and have to say what this company requires — not
    // what a framework's defaults are.
    private static IdentityError Describe(PasswordRefusal refusal, PasswordPolicy policy) =>
        refusal switch
        {
            PasswordRefusal.TooShort => new IdentityError
            {
                Code = TooShortCode,
                Description = $"Password must be at least {policy.MinLength} characters.",
            },
            PasswordRefusal.NeedsUppercase => new IdentityError
            {
                Code = NeedsUppercaseCode,
                Description = "Password must contain an uppercase letter.",
            },
            PasswordRefusal.NeedsNumber => new IdentityError
            {
                Code = NeedsNumberCode,
                Description = "Password must contain a number.",
            },
            PasswordRefusal.NeedsSymbol => new IdentityError
            {
                Code = NeedsSymbolCode,
                Description = "Password must contain a special character.",
            },
            _ => new IdentityError
            {
                Code = PreviouslyUsedCode,
                Description = policy.PreventReuse == 1
                    ? "Password cannot be the one you are using now."
                    : $"Password cannot be any of your last {policy.PreventReuse}.",
            },
        };
}
