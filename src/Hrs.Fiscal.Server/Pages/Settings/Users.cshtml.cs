using Hrs.Fiscal.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Hrs.Fiscal.Server.Pages.Settings;

public sealed class UsersModel(UserStore users) : PageModel
{
    public sealed class Form
    {
        public long Id { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Role { get; set; } = Roles.Cashier;
        public bool Active { get; set; } = true;
        public string? Password { get; set; }
    }

    [BindProperty] public Form Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public long? Edit { get; set; }
    [BindProperty(SupportsGet = true)] public bool Add { get; set; }
    public bool FormOpen => Add || Input.Id != 0 || !ModelState.IsValid;
    public IReadOnlyList<AppUser> All { get; private set; } = [];

    public async Task OnGetAsync()
    {
        All = await users.AllAsync();
        if (Edit is { } id && All.SingleOrDefault(u => u.Id == id) is { } u)
            Input = new Form { Id = u.Id, Username = u.Username, DisplayName = u.DisplayName, Role = u.Role, Active = u.Active };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        All = await users.AllAsync();
        var me = User.Identity!.Name!;
        try
        {
            if (Input.Id == 0)
            {
                if (string.IsNullOrWhiteSpace(Input.Username) || string.IsNullOrWhiteSpace(Input.DisplayName) || string.IsNullOrEmpty(Input.Password))
                    throw new ArgumentException("Username, name and password are required for a new user.");
                await users.CreateAsync(Input.Username, Input.DisplayName.Trim(), Input.Role, Input.Password, me);
            }
            else
            {
                var target = All.Single(u => u.Id == Input.Id);
                if (target.Username == me && (!Input.Active || Input.Role != Roles.Admin))
                    throw new ArgumentException("You cannot remove your own admin access or deactivate yourself.");
                await users.UpdateAsync(Input.Id, Input.DisplayName.Trim(), Input.Role, Input.Active, Input.Password, me);
            }
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return Page();
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation)
        {
            ModelState.AddModelError("", "Username must be 3–64 lower-case characters and not already taken.");
            return Page();
        }
        TempData["Message"] = "User saved and logged.";
        return RedirectToPage(new { Edit = (long?)null });
    }
}
