using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Api.Http;
using Motee.Application.Settings;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// Settings and configuration.
//
// Narrower than the screen, because most of that screen already had a backend
// and links to it: company holidays and blackout periods are leave policies,
// permissions are access levels, approvals are approval templates, and working
// patterns, holiday allowance, presence check-ins and deduction rules all live
// in the modules that own them. What was missing was anything that read the
// switches on the rest of it.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/settings")]
public class SettingsController(ISettingsService settings) : ApiControllerBase
{
    internal const string Module = "admin.settings";

    // Which parts of the product this company has switched on.
    //
    // The onboarding wizard has written these since it shipped and nothing had
    // ever read them, so a company could switch Training off in two places and
    // every training endpoint stayed open. The permission check reads them now.
    [HttpGet("modules")]
    [ProducesResponseType<ModuleSettings>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Modules(CancellationToken cancellationToken) =>
        Ok(await settings.ModulesAsync(cancellationToken));

    [HttpPut("modules")]
    [ProducesResponseType<ModuleSettings>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SaveModules(
        ModuleSettingsRequest request,
        CancellationToken cancellationToken) =>
        Respond(await settings.SaveModulesAsync(request, cancellationToken), "Modules updated.");

    [HttpGet("password-policy")]
    [ProducesResponseType<PasswordPolicyView>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> PasswordPolicy(CancellationToken cancellationToken) =>
        Ok(await settings.PasswordPolicyAsync(cancellationToken));

    [HttpPut("password-policy")]
    [ProducesResponseType<PasswordPolicyView>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SavePasswordPolicy(
        PasswordPolicyRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await settings.SavePasswordPolicyAsync(request, cancellationToken),
            "Password policy saved.");

    // The holiday year cycles, derived from the day the company's year turns
    // over — which is the same turnover every leave balance computes against.
    [HttpGet("holiday-years")]
    [ProducesResponseType<HolidayYearSettings>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> HolidayYears(CancellationToken cancellationToken) =>
        Ok(await settings.HolidayYearsAsync(cancellationToken));

    // One turnover, not a list of cycles. Moving it moves which year everybody's
    // leave is filed under, which is why there is one number to get right rather
    // than a label, two dates and an active flag that can disagree with it.
    [HttpPut("holiday-year")]
    [ProducesResponseType<HolidayYearSettings>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SaveHolidayYear(
        HolidayYearRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await settings.SaveHolidayYearAsync(request, cancellationToken),
            "Holiday year updated.");

    [HttpGet("subscription")]
    [ProducesResponseType<SubscriptionView>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Subscription(CancellationToken cancellationToken) =>
        Ok(await settings.SubscriptionAsync(cancellationToken));

    private IActionResult Respond<T>(SettingsResult<T> result, string message) =>
        result.Succeeded
            ? Ok(result.Value!, message)
            : Failure<T>(StatusFor(result.Outcome), MessageFor(result));

    internal static string StatusFor(SettingsOutcome outcome) => outcome switch
    {
        SettingsOutcome.NotFound => MoteeStatusCodes.NotFound,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor<T>(SettingsResult<T> result) => result.Outcome switch
    {
        SettingsOutcome.NotFound => "Company not found.",
        _ => result.Refusal switch
        {
            SettingsRefusal.TurnoverNotADate =>
                "The holiday year has to start on a real date — a month from 1 to 12 and a "
                + "day that month has.",
            SettingsRefusal.UnknownFeature => "One of those modules does not exist.",
            SettingsRefusal.UnknownTimeZone => "That is not a time zone we recognise.",
            SettingsRefusal.UnknownLanguage => "That is not a language we offer.",
            _ => "That change was not accepted.",
        },
    };
}
