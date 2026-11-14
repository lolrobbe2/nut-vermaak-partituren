
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.MicrosoftAccount;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace nut_vermaak_partituren.src.controllers;

[Route("auth")]
public sealed class AuthController : Controller
{
    [HttpGet("login/{provider}")]
    [AllowAnonymous]
    public IActionResult Login(
        string provider,
        [FromQuery] string? returnUrl = null)
    {
        string authenticationScheme = provider.ToLowerInvariant() switch
        {
            "google" => GoogleDefaults.AuthenticationScheme,
            "microsoft" => MicrosoftAccountDefaults.AuthenticationScheme,
            _ => string.Empty
        };

        if (string.IsNullOrEmpty(authenticationScheme))
        {
            return NotFound();
        }

        string redirectUrl = Url.IsLocalUrl(returnUrl)
            ? returnUrl!
            : "/";

        AuthenticationProperties properties =
            new AuthenticationProperties
            {
                RedirectUri = redirectUrl
            };

        return Challenge(properties, authenticationScheme);
    }

    [HttpPost("logout")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(
            CookieAuthenticationDefaults.AuthenticationScheme);

        return Redirect("/login");
    }
}
