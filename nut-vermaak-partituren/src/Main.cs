using DocumentFormat.OpenXml.Office2010.ExcelAc;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;


Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddMemoryCache()
    .AddRazorPages()
    .AddRazorRuntimeCompilation().WithRazorPagesRoot("/frontend/pages");
builder.Services.AddScoped(sp =>
{
    NavigationManager navigation = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(navigation.BaseUri) };
})
;





builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddServerSideBlazor();
builder.Services.AddSwaggerGen(c =>
{
});
builder.Services.AddCors(options =>
{
   
});
var host = builder.Build();

if (host.Environment.IsDevelopment())
{
    host.MapOpenApi();
    host.UseSwaggerUI(options => {
        options.SwaggerEndpoint("/openapi/v1.json", "VOID api");
        
        }
    );
}
host.MapControllers();
host.MapRazorPages();
host.UseRouting();
host.MapBlazorHub();
host.MapFallbackToPage("/_Host");
if (host.Environment.IsDevelopment())
{
    /*
    host.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(
            Path.Combine(Directory.GetCurrentDirectory(), "obj", "Debug", "net10.0", "win-x64", "scopedcss", "bundle")),
        RequestPath = "/css"
    });
    */
} 
else
{
    /*
    host.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(
          Path.Combine(host.Environment.ContentRootPath, "wwwroot")),
        RequestPath = "/css"
    });
    */
}
if (host.Environment.IsDevelopment())
{
    host.Use((context, next) =>
    {
        // De browser stuurt een preflight met deze header
        if (context.Request.Method == "OPTIONS" &&
            context.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
        {
            context.Response.Headers.Append("Access-Control-Allow-Private-Network", "true");
        }
        return next();
    });
}

host.UseCors("DiscordActivity");
// Add commands from modules
host.UseAuthentication();
host.UseAuthorization();
await host.RunAsync();