using Microsoft.AspNetCore.Identity;

namespace Motee.Infrastructure.Identity;

// Replaces Identity's default UserValidator, which validates a username and calls
// FindByNameAsync to check it is free. Motee has no username — it is always the
// email — and the user_name columns are not mapped, so that lookup would query a
// column the database does not have.
internal sealed class EmailOnlyUserValidator : IUserValidator<ApplicationUser>
{
    public async Task<IdentityResult> ValidateAsync(
        UserManager<ApplicationUser> manager,
        ApplicationUser user)
    {
        List<IdentityError> errors = [];
        string? email = await manager.GetEmailAsync(user);

        if (string.IsNullOrWhiteSpace(email))
        {
            errors.Add(manager.ErrorDescriber.InvalidEmail(email));
            return IdentityResult.Failed([.. errors]);
        }

        ApplicationUser? existing = await manager.FindByEmailAsync(email);

        if (existing is not null && !string.Equals(
                await manager.GetUserIdAsync(existing),
                await manager.GetUserIdAsync(user),
                StringComparison.Ordinal))
        {
            errors.Add(manager.ErrorDescriber.DuplicateEmail(email));
        }

        return errors.Count == 0 ? IdentityResult.Success : IdentityResult.Failed([.. errors]);
    }
}
