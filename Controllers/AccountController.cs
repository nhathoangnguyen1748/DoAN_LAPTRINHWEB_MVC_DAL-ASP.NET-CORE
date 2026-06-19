using DoAnLapTrinhWeb.Models.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoAnLapTrinhWeb.Controllers;

public class AccountController : Controller
{
    private readonly IConfiguration _configuration;

    public AccountController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(GetSafeReturnUrl(returnUrl));
        }

        return View(new LoginViewModel
        {
            ReturnUrl = GetSafeReturnUrl(returnUrl),
            IsGoogleConfigured = IsGoogleConfigured(),
            Message = TempData["LoginMessage"] as string
        });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public IActionResult Google(string? returnUrl = null)
    {
        if (!IsGoogleConfigured())
        {
            TempData["LoginMessage"] = "Google OAuth is not configured yet.";
            return RedirectToAction(nameof(Login), new { returnUrl });
        }

        var properties = new AuthenticationProperties
        {
            RedirectUri = GetSafeReturnUrl(returnUrl)
        };
        return Challenge(properties, GoogleDefaults.AuthenticationScheme);
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    public IActionResult Denied()
    {
        return View("Login", new LoginViewModel
        {
            ReturnUrl = Url.Action("Index", "Projects") ?? "/",
            IsGoogleConfigured = IsGoogleConfigured(),
            Message = "You do not have permission to open that project."
        });
    }

    private bool IsGoogleConfigured()
    {
        return !string.IsNullOrWhiteSpace(_configuration["Authentication:Google:ClientId"]) &&
               !string.IsNullOrWhiteSpace(_configuration["Authentication:Google:ClientSecret"]);
    }

    private string GetSafeReturnUrl(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return returnUrl;
        }

        return Url.Action("Index", "Projects") ?? "/";
    }
}
