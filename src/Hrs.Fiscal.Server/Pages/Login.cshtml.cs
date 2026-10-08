using System.Security.Claims;
using Hrs.Fiscal.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages;

public sealed class LoginModel(UserStore users, AuditLog audit) : PageModel
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var username = Username.Trim().ToLowerInvariant();
        if (await audit.CountRecentAsync(AuditLog.Actions.LoginFailed, username, Window) >= MaxFailures)
        {
            Error = "Too many failed attempts. Try again in 15 minutes or ask an administrator.";
            return Page();
        }

        var user = await users.ValidateAsync(username, Password);
        if (user is null)
        {
            await audit.WriteAsync("anonymous", AuditLog.Actions.LoginFailed, "app_user", username,
                new { ip = HttpContext.Connection.RemoteIpAddress?.ToString() });
            Error = "Username or password is incorrect.";
            return Page();
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, user.Username),
            new Claim("display_name", user.DisplayName),
            new Claim(ClaimTypes.Role, user.Role),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        await audit.WriteAsync(user.Username, AuditLog.Actions.Login, "app_user", user.Username,
            new { ip = HttpContext.Connection.RemoteIpAddress?.ToString() });

        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }
}
