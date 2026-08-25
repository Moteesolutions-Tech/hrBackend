namespace Motee.Application.Auth;

public interface IUserLookup
{
    Task<Guid?> FindIdByEmailAsync(string email, CancellationToken cancellationToken = default);
}
