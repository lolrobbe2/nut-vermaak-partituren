
using Google.Apis.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using nut_vermaak_partituren.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace nut_vermaak_partituren.src.controllers;

[Authorize]
[Route("admin/storage/google")]
public sealed class GoogleDriveAuthorizationController : Controller
{
    private const string StateCookieName = "GoogleDriveOAuthState";

    private const string AuthorizationEndpoint =
        "https://accounts.google.com/o/oauth2/v2/auth";

    private const string TokenEndpoint =
        "https://oauth2.googleapis.com/token";

    private readonly IHttpClientFactory _httpClientFactory;

    public GoogleDriveAuthorizationController(
        IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet("connect")]
    public IActionResult Connect()
    {
        if (!IsRootUser())
        {
            return Forbid();
        }

        byte[] stateBytes = RandomNumberGenerator.GetBytes(32);
        string state = WebEncoders.Base64UrlEncode(stateBytes);

        Response.Cookies.Append(
            StateCookieName,
            state,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                MaxAge = TimeSpan.FromMinutes(10),
                Path = "/admin/storage/google"
            });

        Dictionary<string, string?> parameters = new()
        {
            ["client_id"] = AppEnv.GoogleClientId,
            ["redirect_uri"] = AppEnv.GoogleDriveRedirectUri,
            ["response_type"] = "code",

            // Full Drive access: needed to access your existing
            // My Drive folder directly by its folder ID.
            ["scope"] =
                "openid email https://www.googleapis.com/auth/drive.file",

            ["access_type"] = "offline",
            ["prompt"] = "consent select_account",
            ["login_hint"] = AppEnv.RootAddress,
            ["include_granted_scopes"] = "true",
            ["state"] = state
        };

        string authorizationUrl = QueryHelpers.AddQueryString(
            AuthorizationEndpoint,
            parameters);

        return Redirect(authorizationUrl);
    }

    [HttpGet("callback")]
    [ResponseCache(
        NoStore = true,
        Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        if (!IsRootUser())
        {
            return Forbid();
        }

        string? expectedState =
            Request.Cookies[StateCookieName];

        Response.Cookies.Delete(
            StateCookieName,
            new CookieOptions
            {
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/admin/storage/google"
            });

        if (string.IsNullOrWhiteSpace(expectedState) ||
            string.IsNullOrWhiteSpace(state) ||
            !StateMatches(expectedState, state))
        {
            return BadRequest("Invalid OAuth state.");
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            return BadRequest(
                $"Google authorization failed: {error}");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return BadRequest("Authorization code is missing.");
        }

        HttpClient httpClient =
            _httpClientFactory.CreateClient();

        using FormUrlEncodedContent form =
            new(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = AppEnv.GoogleClientId,
                ["client_secret"] = AppEnv.GoogleClientSecret,
                ["redirect_uri"] = AppEnv.GoogleDriveRedirectUri,
                ["grant_type"] = "authorization_code"
            });

        using HttpResponseMessage response =
            await httpClient.PostAsync(
                TokenEndpoint,
                form,
                cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return StatusCode(
                502,
                "Google token exchange failed. Check your OAuth configuration.");
        }

        await using Stream responseStream =
            await response.Content.ReadAsStreamAsync(
                cancellationToken);

        using JsonDocument document =
            await JsonDocument.ParseAsync(
                responseStream,
                cancellationToken: cancellationToken);

        JsonElement tokenResponse = document.RootElement;

        if (!tokenResponse.TryGetProperty(
                "id_token",
                out JsonElement idTokenElement) ||
            !tokenResponse.TryGetProperty(
                "refresh_token",
                out JsonElement refreshTokenElement))
        {
            return BadRequest(
                "Google did not return the required tokens. " +
                "Revoke the previous authorization and reconnect.");
        }

        string? idToken = idTokenElement.GetString();
        string? refreshToken = refreshTokenElement.GetString();

        if (string.IsNullOrWhiteSpace(idToken) ||
            string.IsNullOrWhiteSpace(refreshToken))
        {
            return BadRequest("Google returned an empty token.");
        }

        GoogleJsonWebSignature.Payload payload =
            await GoogleJsonWebSignature.ValidateAsync(
                idToken,
                new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = new[] { AppEnv.GoogleClientId }
                });

        if (!payload.EmailVerified ||
            !string.Equals(
                payload.Email,
                AppEnv.RootAddress,
                StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        Response.Headers.CacheControl =
            "no-store, no-cache, must-revalidate";

        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Content-Type-Options"] = "nosniff";

        return Content(
            "Google Drive connected successfully.\n\n" +
            $"Account: {payload.Email}\n\n" +
            "Add the following value to your Render environment:\n\n" +
            "GOOGLE_DRIVE_REFRESH_TOKEN=\n\n" +
            refreshToken,
            "text/plain");
    }

    private bool IsRootUser()
    {
        string? email =
            User.FindFirstValue(ClaimTypes.Email) ??
            User.FindFirstValue("email");

        return !string.IsNullOrWhiteSpace(email) &&
            string.Equals(
                email,
                AppEnv.RootAddress,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool StateMatches(
        string expected,
        string actual)
    {
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] actualBytes = Encoding.UTF8.GetBytes(actual);

        return expectedBytes.Length == actualBytes.Length &&
            CryptographicOperations.FixedTimeEquals(
                expectedBytes,
                actualBytes);
    }
}
