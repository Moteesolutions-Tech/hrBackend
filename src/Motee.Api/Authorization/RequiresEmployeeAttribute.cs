using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Motee.Api.Contracts;
using Motee.Application.Common;

namespace Motee.Api.Authorization;

// Marks an endpoint that only makes sense for somebody who works here.
//
// Self-service — your leave, your balance, your onboarding, cover for your approvals —
// is for every employee, whatever they do: the HR admin, a new joiner, anybody holding
// no access level at all. That is why these endpoints carry no permission attribute.
// The one account it cannot serve is the company's owner, who runs the account rather
// than working there and so has no employee record to be the subject of any of it.
//
// A filter rather than a policy, because this is not a permission question. The owner
// is not short of rights — they have every one there is. There is simply nothing of
// theirs at this address, and evaluating that through the permission handler would mean
// teaching it to answer a question about account shape.
//
// Before this, each endpoint answered for itself with a 404 reading "No employee record
// found." Three problems: it read as missing data rather than a rule, it was worded
// differently in each place, and a client could not tell it apart from a genuine 404.
//
// An AuthorizeAttribute as well as a filter, and both halves are needed.
//
// These endpoints had no authorization attribute at all, and the application sets no
// fallback policy, so they were reachable without a token: an anonymous caller got the
// same answer as somebody with no employee record. Nothing leaked, because nobody
// resolves for an anonymous request and every one of these is scoped to whoever that
// would have been — but "nothing leaked" was a consequence of the queries rather than
// a decision, and the delegation list answered 200 with an empty body to a stranger.
//
// With no policy named, this requires an authenticated user and nothing more, which is
// the correct bar: self-service asks who you are, never what you may do. Authorization
// runs before action filters, so an anonymous caller gets 401 and never reaches the
// employee check — which also keeps it from being told what an owner account is.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequiresEmployeeAttribute : AuthorizeAttribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        ICurrentEmployee currentEmployee =
            context.HttpContext.RequestServices.GetRequiredService<ICurrentEmployee>();

        if (await currentEmployee.IdAsync(context.HttpContext.RequestAborted) is not null)
        {
            await next();
            return;
        }

        context.Result = new ObjectResult(
            ServiceResponse<object?>.Failure(MoteeStatusCodes.NotAnEmployee, Message))
        {
            StatusCode = MoteeStatusCodes.ToHttpStatus(MoteeStatusCodes.NotAnEmployee),
        };
    }

    // Says which account this is and why, rather than describing an absence. Somebody
    // reading it should stop looking for the missing record.
    internal const string Message =
        "This account manages the company rather than working in it, so it has no "
        + "employee record and nothing of its own here.";
}
