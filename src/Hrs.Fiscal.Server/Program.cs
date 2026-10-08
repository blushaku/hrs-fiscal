// HRS Fiscal Server — one per property.
//
// Modules:
//   Pages/       admin web UI (Razor Pages): dashboard, receipts, reprint, audit log + integrity check,
//                exports (CSV/PDF), settings (business, workstations, OPERA mapping, VAT, users, retention, ATK)
//   Data/        PostgreSQL access; db/migrations are applied on startup
//   Services/    exports, QR rendering, demo data
// Planned:
//   Flip/        LAN endpoint called by Oracle FLIP (OFIS on-premise). Contract pending Oracle's specification.
//   Routing/     OPERA Fiscal Terminal ID -> HRS Fiscal Client (PosId)
//   Queue/       offline queue re-send, 48h / 10th-of-month alerts
//
// Command line:
//   Hrs.Fiscal.Server                          run the server (also as a Windows service)
//   Hrs.Fiscal.Server create-user <user> <role> [display name]   create a user; password read from stdin
//   Hrs.Fiscal.Server seed-demo                fill an EMPTY database with signed demo receipts

using System.Security.Claims;
using Hrs.Fiscal.Server;
using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

builder.Services.AddSingleton<PropertyClock>();
builder.Services.AddFiscalDatabase(builder.Configuration);
builder.Services.AddScoped<Exporter>();
builder.Services.AddScoped<DemoSeeder>();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Login";
        o.AccessDeniedPath = "/Denied";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Cookie.Name = "hrs-fiscal";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin))
    .AddPolicy(Policies.Export, p => p.RequireRole(Roles.Supervisor, Roles.Admin, Roles.Auditor))
    .AddPolicy(Policies.Reprint, p => p.RequireRole(Roles.Cashier, Roles.Supervisor, Roles.Admin))
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AllowAnonymousToPage("/Login");
    o.Conventions.AuthorizeFolder("/Settings", Policies.Admin);
    o.Conventions.AuthorizePage("/Export/Index", Policies.Export);
});

var app = builder.Build();

// ---- command-line tools ---------------------------------------------------------------------
if (args.Length > 0 && args[0] is "create-user" or "seed-demo")
{
    await MigrateAsync(app);
    using var scope = app.Services.CreateScope();
    if (args[0] == "create-user")
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: create-user <username> <role> [display name]"); return 2; }
        Console.Write("Password: ");
        var password = Console.ReadLine() ?? "";
        await scope.ServiceProvider.GetRequiredService<UserStore>()
            .CreateAsync(args[1], args.Length > 3 ? string.Join(' ', args[3..]) : args[1], args[2], password, "cli");
        Console.WriteLine($"User '{args[1]}' created.");
    }
    else
    {
        var n = await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync();
        Console.WriteLine($"Demo data created: {n} receipts.");
    }
    return 0;
}

await MigrateAsync(app);
await BootstrapAdminAsync(app);

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", component = "hrs-fiscal-server" })).AllowAnonymous();

// Endpoint called by Oracle FLIP over the hotel LAN (OPERA: Fiscal Folio parameter "FLIP Server Address",
// Fiscal Terminals "Address and Port"). Protocol/contract pending Oracle's FLIP fiscal-partner specification.
app.MapPost("/flip/fiscal-payload", () => Results.StatusCode(StatusCodes.Status501NotImplemented)).AllowAnonymous();

app.MapGet("/receipts/{id:long}/qr.svg", async (long id, ReceiptQueries q) =>
{
    var r = await q.GetAsync(id);
    return r is null ? Results.NotFound() : Results.Text(QrRenderer.Svg(r.QrString), "image/svg+xml");
});

app.MapGet("/receipts/{id:long}/copy.pdf", async (long id, Exporter exporter, HttpContext http) =>
{
    var result = await exporter.ReceiptCopyPdfAsync(id, http.User.Identity!.Name!, http.RequestAborted);
    return result is null ? Results.NotFound() : Results.File(result.Content, result.ContentType, result.FileName);
});

app.MapGet("/export/{dataset}", async (string dataset, string format, DateOnly? from, DateOnly? to, string? requestedBy,
        Exporter exporter, HttpContext http) =>
    {
        var result = await exporter.ExportAsync(dataset, format, from, to, requestedBy, http.User.Identity!.Name!, http.RequestAborted);
        return result is null ? Results.BadRequest() : Results.File(result.Content, result.ContentType, result.FileName);
    })
    .RequireAuthorization(Policies.Export);

app.MapPost("/logout", async (HttpContext http, AuditLog audit) =>
{
    await audit.WriteAsync(http.User.Identity?.Name ?? "?", AuditLog.Actions.Logout);
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/Login");
});

app.MapRazorPages();
app.Run();
return 0;

static async Task MigrateAsync(WebApplication app)
{
    if (!app.Configuration.GetValue("Database:MigrateOnStartup", true)) return;
    var dir = Path.Combine(AppContext.BaseDirectory, "migrations");
    await app.Services.GetRequiredService<Migrator>().MigrateAsync(dir);
}

// First start: create the admin account from configuration (Bootstrap:AdminPassword), once.
static async Task BootstrapAdminAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<UserStore>();
    if (await users.AnyAsync()) return;
    var password = app.Configuration["Bootstrap:AdminPassword"];
    if (string.IsNullOrEmpty(password))
    {
        app.Logger.LogWarning("No users exist. Set Bootstrap:AdminPassword or run 'Hrs.Fiscal.Server create-user admin Admin'.");
        return;
    }
    await users.CreateAsync("admin", "Administrator", Roles.Admin, password, "bootstrap");
    app.Logger.LogWarning("Created initial 'admin' user. Remove Bootstrap:AdminPassword from configuration now.");
}

public static class Policies
{
    public const string Admin = "Admin", Export = "Export", Reprint = "Reprint";
}

public static class UserExtensions
{
    public static string Display(this ClaimsPrincipal user) => user.FindFirstValue("display_name") ?? user.Identity?.Name ?? "";
    public static string Role(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Role) ?? "";
}

public partial class Program;
