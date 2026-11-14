
using Google.Apis.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Util.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using nut_vermaak_partituren.Configuration;
using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace nut_vermaak_partituren.Controllers;

[AllowAnonymous]
[Route("admin/storage/google")]
public sealed class GoogleDriveAuthorizationController : Controller
{
    private const string StateCookieName = "GoogleDriveOAuthState";
    private readonly IMemoryCache _memoryCache;

    public GoogleDriveAuthorizationController(IMemoryCache memoryCache)
    {
        _memoryCache = memoryCache;
    }
    [HttpGet("connect")]
    public IActionResult Connect()
    {
        string redirectUri = GetRedirectUri();

        GoogleAuthorizationCodeFlow flow = CreateFlow();

        string state = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32));

        Response.Cookies.Append(
            StateCookieName,
            state,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(10)
            });
        GoogleAuthorizationCodeRequestUrl authorizationRequest =
           (GoogleAuthorizationCodeRequestUrl)
        flow.CreateAuthorizationCodeRequest(redirectUri);

        authorizationRequest.State = state;
        authorizationRequest.AccessType = "offline";
        authorizationRequest.Prompt = "consent select_account";

        authorizationRequest.Scope = string.Join(
            " ",
            new[]
            {
                "openid",
                "email",
                DriveService.Scope.DriveFile,
                "https://www.googleapis.com/auth/drive.appdata"
            });

        return Redirect(authorizationRequest.Build().ToString());
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return BadRequest($"Google authorization failed: {error}");
        }

        string? expectedState =
            Request.Cookies[StateCookieName];

        Response.Cookies.Delete(StateCookieName);

        if (string.IsNullOrWhiteSpace(state) ||
            string.IsNullOrWhiteSpace(expectedState) ||
            !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(state),
                System.Text.Encoding.UTF8.GetBytes(expectedState)))
        {
            return BadRequest("Invalid OAuth state.");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return BadRequest("Google did not return an authorization code.");
        }

        GoogleAuthorizationCodeFlow flow = CreateFlow();

        TokenResponse token = await flow.ExchangeCodeForTokenAsync(
            "root-admin",
            code,
            GetRedirectUri(),
            CancellationToken.None);

        if (string.IsNullOrWhiteSpace(token.IdToken))
        {
            return BadRequest("Google did not return an ID token.");
        }

        GoogleJsonWebSignature.Payload payload =
            await GoogleJsonWebSignature.ValidateAsync(token.IdToken);

        string rootAddress = AppEnv.RootAddress;

        if (!string.Equals(
            payload.Email,
            rootAddress,
            StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            return Content(
                "Google did not return a refresh token. Revoke the application's access in your Google Account and try connecting again.",
                "text/plain");
        }

        string ticket = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(32));

        _memoryCache.Set(
            $"google-drive-refresh-token:{ticket}",
            token.RefreshToken,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
            });

        Response.Headers.CacheControl = "no-store, no-cache";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";

        return Redirect(
            $"/admin/storage/google/refresh-token?ticket={ticket}");
    }

    private static GoogleAuthorizationCodeFlow CreateFlow()
    {
        return new GoogleAuthorizationCodeFlow(
            new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = AppEnv.GoogleClientId,
                    ClientSecret = AppEnv.GoogleClientSecret
                },
                Scopes = new[]
                {
                    "openid",
                    "email",
                    DriveService.Scope.DriveFile,
                    "https://www.googleapis.com/auth/drive.appdata"
                },
                DataStore = new NullDataStore()
            });
    }

    private string GetRedirectUri()
    {
        return $"{Request.Scheme}://{Request.Host}/admin/storage/google/callback";
    }
}
