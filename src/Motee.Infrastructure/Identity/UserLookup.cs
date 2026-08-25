using Microsoft.EntityFrameworkCore;
using Motee.Application.Auth;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Identity;

internal sealed class UserLookup(MoteeDbContext dbContext) : IUserLookup
{
    public async Task<Guid?> FindIdByEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        // Identity stores the uppercase form and indexes it, so matching on
        // normalized_email uses ix_users_email rather than scanning every row.
        string normalised = email.Trim().ToUpperInvariant();

        return await dbContext.Users
            .AsNoTracking()
            .Where(user => user.NormalizedEmail == normalised)
            .Select(user => (Guid?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
