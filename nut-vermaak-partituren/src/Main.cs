using DocumentFormat.OpenXml.Office2010.ExcelAc;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using nut_vermaak_partituren.Configuration;
using nut_vermaak_partituren.FileSystem;
using nut_vermaak_partituren.Infrastructure.GoogleDrive;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;


Console.OutputEncoding = System.Text.Encoding.UTF8;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
AppEnv.Load(builder.Environment.IsDevelopment());
builder.Services
    .AddMemoryCache()
    .AddRazorPages()
    .AddRazorRuntimeCompilation()
    .WithRazorPagesRoot("/frontend/pages");

builder.Services.AddScoped(sp =>
{
    NavigationManager navigation = sp.GetRequiredService<NavigationManager>();
    return new HttpClient
    {
        BaseAddress = new Uri(navigation.BaseUri)
    };
});

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<DriveService>(_ =>
{
    return GoogleDriveServiceFactory.Create(
        AppEnv.GoogleDriveClientEmail,
        AppEnv.GoogleDrivePrivateKey,
        "Nut Vermaak Partituren");
});

builder.Services.AddSingleton<IFileSystem>(serviceProvider =>
{
    DriveService driveService =
        serviceProvider.GetRequiredService<DriveService>();

    string rootFolderId = AppEnv.GoogleDriveRootFolderId;

    return new GoogleDriveFileSystem(
        driveService,
        rootFolderId);
});
builder.Services.AddBlazorBootstrap();

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddSwaggerGen(c =>
{
});

// Keep your CORS policy, but make sure "DiscordActivity"
// is actually registered if you use it.
builder.Services.AddCors(options =>
{
    // Register your DiscordActivity policy here.
});

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme =
            CookieAuthenticationDefaults.AuthenticationScheme;

        options.DefaultSignInScheme =
            CookieAuthenticationDefaults.AuthenticationScheme;

        options.DefaultChallengeScheme =
            CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.Cookie.Name = "MyApp.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;

        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    })
    .AddGoogle(options =>
    {
        options.ClientId = AppEnv.GoogleClientId;
        options.ClientSecret = AppEnv.GoogleClientSecret;
        options.SignInScheme =
            CookieAuthenticationDefaults.AuthenticationScheme;
        options.CallbackPath = "/signin-google";
    })
    .AddMicrosoftAccount(options =>
    {
        options.ClientId = AppEnv.MicrosoftClientId;
        options.ClientSecret = AppEnv.MicrosoftClientSecret;
        options.SignInScheme =
            CookieAuthenticationDefaults.AuthenticationScheme;
        options.CallbackPath = "/signin-microsoft";
    });

WebApplication host = builder.Build();

if (host.Environment.IsDevelopment())
{
    host.MapOpenApi();

    host.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "VOID api");
    });
}

// Middleware
host.UseHttpsRedirection();
host.UseStaticFiles();

host.UseRouting();

if (host.Environment.IsDevelopment())
{
    host.Use((context, next) =>
    {
        if (context.Request.Method == "OPTIONS" &&
            context.Request.Headers.ContainsKey(
                "Access-Control-Request-Private-Network"))
        {
            context.Response.Headers.Append(
                "Access-Control-Allow-Private-Network", "true");
        }

        return next();
    });
}

host.UseAuthentication();
host.UseAuthorization();

host.UseAntiforgery();

// Endpoints
host.MapControllers();
host.MapRazorPages();
host.MapStaticAssets();

host.MapRazorComponents<nut_vermaak_partituren.frontend.pages.Components.App>()
    .AddInteractiveServerRenderMode();

await host.RunAsync();