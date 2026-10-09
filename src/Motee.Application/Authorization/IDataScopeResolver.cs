using Motee.Domain.Authorization;

namespace Motee.Application.Authorization;

// Turns a self-relative scope into a named one, using the holder's own record.
//
// This exists so that nothing downstream has to know self-relative scopes are a thing. A
// query layer that handled OwnDepartment would need the viewer's department threaded
// into every query object, and every module added later would have to remember to do it
// — the kind of obligation that is met in three places out of four.
//
// Resolving once, where the scope is first worked out for a request, means the queries
// only ever see Department or Branch with ids in them.
public interface IDataScopeResolver
{
    // A holder with no department or no branch resolves to the named kind with an empty
    // list, which reaches nothing. That is the right answer rather than an awkward one: a
    // level scoped to "their own department" genuinely grants nothing to somebody who is
    // in none, and failing open would hand them the company.
    Task<DataScope> ResolveAsync(
        DataScope scope,
        Guid? holderEmployeeId,
        CancellationToken cancellationToken = default);
}
