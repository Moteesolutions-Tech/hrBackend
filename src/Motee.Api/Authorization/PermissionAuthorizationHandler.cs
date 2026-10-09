using Microsoft.AspNetCore.Authorization;
using Motee.Application.Auth;
using Motee.Application.Authorization;
using Motee.Domain.Authorization;

namespace Motee.Api.Authorization;

internal sealed class PermissionAuthorizationHandler(
    IHttpContextAccessor httpContextAccessor,
    IUserPermissions userPermissions,
    IDataScopeResolver scopeResolver) : AuthorizationHandler<PermissionRequirement>
{
    // Where the granted breadth is left for the endpoint to read. Passing the check
    // is not the whole answer — a team-scoped grant still has to narrow its query.
    public const string ScopeItemKey = "Motee.PermissionScope";

    // The full reach, including the departments or business units a named scope
    // covers. Breadth alone cannot express those, so the endpoint reads this when it
    // needs the ids.
    public const string DataScopeItemKey = "Motee.DataScope";

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        // "sub" is the user id. Permissions are resolved from it rather than carried
        // in the token: a merged set across 42 modules is kilobytes, and it would be
        // frozen at sign-in, so correcting a level would not take effect until every
        // token had expired.
        if (!Guid.TryParse(context.User.FindFirst(MoteeClaimTypes.Subject)?.Value, out Guid userId))
        {
            return;
        }

        ResolvedPermissions held = await userPermissions.ForAsync(userId);

        // The owner bypasses the check rather than holding a level that grants
        // everything. A level enumerating every module goes stale the moment a new
        // one ships; a bypass does not. It skips the check, never the audit.
        DataScope reach = held.IsOwner
            ? DataScope.Everything
            : AccessDecision.Resolve(held.Permissions, requirement.Module, requirement.Action);

        if (reach.Kind == DataScopeKind.None)
        {
            // Left unsucceeded rather than explicitly failed, so another handler
            // could still grant it later.
            return;
        }

        // Self-relative scopes become named ones here, once, before anything downstream
        // sees them. Doing it at the one point the scope is worked out is what keeps the
        // query layer — and every module added after this one — unaware that
        // "their own department" is a thing that exists.
        if (reach.NeedsHolder)
        {
            reach = await scopeResolver.ResolveAsync(reach, HolderEmployeeId(context));
        }

        if (httpContextAccessor.HttpContext is HttpContext http)
        {
            http.Items[ScopeItemKey] = reach.Breadth;
            http.Items[DataScopeItemKey] = reach;
        }

        context.Succeed(requirement);
    }

    // The claim, not a database lookup: it is already in the token, and the resolver
    // reads the employee row anyway when it needs the department or branch.
    private static Guid? HolderEmployeeId(AuthorizationHandlerContext context) =>
        Guid.TryParse(context.User.FindFirst(MoteeClaimTypes.EmployeeId)?.Value, out Guid id)
            ? id
            : null;
}
