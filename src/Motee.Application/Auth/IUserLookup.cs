namespace Motee.Application.Auth;

// Resolves an address to an id for the endpoints that used to take a user id outright.
//
// Deliberately returns only the id and nothing else: callers here are anonymous, and
// handing back a user object invites returning some part of it in a response, which is
// how an endpoint that is careful about disclosure stops being careful.
public interface IUserLookup
{
    Task<Guid?> FindIdByEmailAsync(string email, CancellationToken cancellationToken = default);
}
