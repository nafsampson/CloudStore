//summary//
// Handles hashed-password sign-in, account lockout and antiforgery-protected logout.
//summary//
//-------------------Start Of File------------------//
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using CloudStore.Models;
using CloudStore.Models.Requests;
using CloudStore.Security;

namespace CloudStore.Controllers;

public class LoginController(UserManager<LoginUser> users, SignInManager<LoginUser> signIn) : Controller
{
    [AllowAnonymous, HttpGet]
    public IActionResult Index() => View();

    [AllowAnonymous, HttpPost, EnableRateLimiting("login")]
    public async Task<IActionResult> Index(LoginRequest request)
    {
        if (ModelState.IsValid)
        {
            var user = await users.FindByNameAsync(request.Username);
            if (user is not null && user.IsEnabled && AccessPolicies.IsKnownRole(user.Role))
            {
                // Identity owns hash verification, rehashing and persistent failed-attempt lockout.
                var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
                if (result.Succeeded)
                {
                    await signIn.SignInAsync(user, isPersistent: false);
                    return RedirectToAction("Index", user.Role == AccessPolicies.AdminRole ? "Admin" : "Customer");
                }
            }
        }
        ModelState.Clear();
        ViewBag.Error = "Unable to sign in. Check your details or try again later.";
        return View();
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return RedirectToAction(nameof(Index));
    }

    [AllowAnonymous, HttpGet]
    public IActionResult AccessDenied()
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View();
    }
}
//-------------------End Of File------------------//
