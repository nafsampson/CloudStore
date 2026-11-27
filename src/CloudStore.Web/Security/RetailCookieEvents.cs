//summary//
// Rejects stale cookies after an account is disabled, removed, renamed or changes role/password.
//summary//
//-------------------Start Of File------------------//
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using CloudStore.Models;

namespace CloudStore.Security;

public class RetailCookieEvents(UserManager<LoginUser> manager) : CookieAuthenticationEvents
{
    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var user = principal is null ? null : await manager.GetUserAsync(principal);
        var stamp = principal?.FindFirstValue("AspNet.Identity.SecurityStamp");
        if (user is null || !user.IsEnabled || !AccessPolicies.IsKnownRole(user.Role)
            || principal?.FindFirstValue(ClaimTypes.Role) != user.Role
            || principal?.Identity?.Name != user.UserName
            || string.IsNullOrEmpty(stamp) || stamp != user.SecurityStamp)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
    }
}
//-------------------End Of File------------------//
