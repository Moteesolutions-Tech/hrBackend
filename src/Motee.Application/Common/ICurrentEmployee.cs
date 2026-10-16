namespace Motee.Application.Common;

// Which employee record the signed-in user is.
//
// Its own service because the query is easy to get subtly wrong: users are deliberately
// not tenant-scoped — login has to find an account before there is a tenant to scope to —
// so every query over them needs its own tenant clause, and a missing one reads across
// companies. That has been a real bug here more than once, so it is written down and
// tested once rather than repeated per module.
public interface ICurrentEmployee
{
    // Null when nobody is signed in, or when the account has no employee record. The
    // account owner is permanently in the second case: they run the company's account
    // rather than working there, so the employee-shaped surfaces have nothing to show
    // them and that is the correct answer rather than a missing row.
    Task<Guid?> IdAsync(CancellationToken cancellationToken = default);
}
