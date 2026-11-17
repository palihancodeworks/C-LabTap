using C_LabTap.Models;
using C_LabTap.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Every controller requires a signed-in staff/admin account by default
// (see AccountController) - AllowAnonymous on AccountController and
// LogbookController (the public kiosk) are the only exceptions.
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AuthorizeFilter());

    // CSRF protection: every POST must carry the anti-forgery token this
    // app issued to the browser, or it's rejected with 400 before the
    // action runs. Forms get the token as a hidden field automatically;
    // fetch() calls send it as a header (see wwwroot/js/csrf.js). The
    // public kiosk opts out - see LogbookController for why.
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
});

// The header name csrf.js sends the token under.
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
});

// URLs render in lowercase everywhere (see chat: "prof said it should
// be all in small letters" - localhost:5066/Attendance should read
// localhost:5066/attendance). This also makes the router match an
// incoming lowercase URL to the right controller/action, so nothing
// about the controllers/links/routes themselves has to change.
builder.Services.Configure<RouteOptions>(options =>
{
    options.LowercaseUrls = true;
});

// "External" is a short-lived cookie that only ever holds a Google
// callback's raw claims for the few seconds between the redirect back
// from Google and AccountController.GoogleResponse deciding what to do
// with them (log in an already-linked account, or link a new one) -
// it never becomes the app's real signed-in session, that's still
// only the main cookie scheme below (see AccountController).
const string ExternalAuthScheme = "External";

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/LoggedOut";
        options.AccessDeniedPath = "/Account/LoggedOut";
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
    })
    .AddCookie(ExternalAuthScheme, options =>
    {
        options.Cookie.Name = "C_LabTap.External";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddGoogle(options =>
    {
        var googleConfig = builder.Configuration.GetSection("Google");
        options.ClientId = googleConfig["ClientId"] ?? "";
        options.ClientSecret = googleConfig["ClientSecret"] ?? "";
        options.SignInScheme = ExternalAuthScheme;
        options.CallbackPath = "/signin-google";
    });

// Supabase: config comes from the "Supabase" section of
// appsettings.Development.json (Url + ServiceRoleKey). This client is
// server-side only - it is never exposed to the browser, and the
// service_role key bypasses Row Level Security by design.
builder.Services.Configure<SupabaseOptions>(builder.Configuration.GetSection("Supabase"));

// Email: config comes from the "Email" section of
// appsettings.Development.json (GmailAddress + AppPassword) - see
// EmailService. Sends OTP codes for password/PIN/Google-link changes.
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddSingleton<EmailService>();

builder.Services.AddHttpClient<SupabaseService>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<SupabaseOptions>>().Value;
    client.BaseAddress = new Uri($"{options.Url.TrimEnd('/')}/rest/v1/");
    client.DefaultRequestHeaders.Add("apikey", options.ServiceRoleKey);
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.ServiceRoleKey}");
    client.DefaultRequestHeaders.Add("Prefer", "return=representation");
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Without this, hitting Back after Logout can show a browser-cached copy
// of an authenticated page instead of re-checking the (now-cleared)
// login cookie. Telling the browser never to cache these responses means
// Back always forces a fresh request, which the auth cookie check then
// correctly bounces to the login page.
app.Use(async (context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Expires"] = "0";
    await next();
});

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
