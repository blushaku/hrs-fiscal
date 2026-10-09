using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages.Settings;

public sealed class RolesModel(RolePermissions roles, AuditLog audit) : PageModel
{
    /// <summary>Editable roles (Admin always has everything).</summary>
    public static readonly string[] Editable = Roles.All.Where(r => r != Roles.Admin).ToArray();

    public IReadOnlyDictionary<string, IReadOnlySet<string>> Matrix { get; private set; } = new Dictionary<string, IReadOnlySet<string>>();

    public async Task OnGetAsync() => Matrix = await roles.MatrixAsync();

    /// <summary>Checkboxes are posted as "Cashier|receipts.view".</summary>
    public async Task<IActionResult> OnPostAsync(string[] grant)
    {
        var posted = grant.Select(g => g.Split('|')).Where(p => p.Length == 2).ToLookup(p => p[0], p => p[1]);
        var changes = 0;
        foreach (var role in Editable)
        {
            var (added, removed) = await roles.SaveAsync(role, posted[role]);
            if (added.Length + removed.Length == 0) continue;
            changes++;
            await audit.WriteAsync(User.Identity!.Name!, AuditLog.Actions.PermissionsChanged, "role", role, new { added, removed });
        }
        TempData["Message"] = changes == 0 ? "No changes." : $"Permissions saved for {changes} role(s) and logged. They apply on the next page a user opens.";
        return RedirectToPage();
    }
}
