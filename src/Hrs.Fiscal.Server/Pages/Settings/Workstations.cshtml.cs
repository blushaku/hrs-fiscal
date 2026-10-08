using System.ComponentModel.DataAnnotations;
using Hrs.Fiscal.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Hrs.Fiscal.Server.Pages.Settings;

public sealed class WorkstationsModel(SettingsStore settings, PropertyClock clock) : PageModel
{
    public sealed class Form
    {
        public long Id { get; set; }
        [Range(1, long.MaxValue, ErrorMessage = "POS ID must be a positive number.")] public long PosId { get; set; }
        [Required(ErrorMessage = "OPERA Fiscal Terminal ID is required.")] public string? OperaTerminalId { get; set; }
        [Required] public string Hostname { get; set; } = "";
        public string? ClientEndpoint { get; set; }
        public string? Description { get; set; }
        [RegularExpression("pending|active|disabled")] public string Status { get; set; } = "pending";
    }

    [BindProperty] public Form Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public long? Edit { get; set; }
    public IReadOnlyList<TerminalEdit> Terminals { get; private set; } = [];
    public PropertyClock Clock => clock;

    public async Task OnGetAsync()
    {
        Terminals = await settings.TerminalsAsync();
        if (Edit is { } id && Terminals.SingleOrDefault(t => t.Id == id) is { } t)
            Input = new Form { Id = t.Id, PosId = t.PosId, OperaTerminalId = t.OperaTerminalId, Hostname = t.Hostname, ClientEndpoint = t.ClientEndpoint, Description = t.Description, Status = t.Status };
        else
            Input = new Form { PosId = Terminals.Count == 0 ? 1 : Terminals.Max(t => t.PosId) + 1 };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Terminals = await settings.TerminalsAsync();
        if (!ModelState.IsValid) return Page();
        try
        {
            await settings.SaveTerminalAsync(new TerminalEdit
            {
                Id = Input.Id, PosId = Input.PosId, OperaTerminalId = Input.OperaTerminalId?.Trim(), Hostname = Input.Hostname.Trim(),
                ClientEndpoint = Input.ClientEndpoint?.Trim(), Description = Input.Description?.Trim(), Status = Input.Status,
            }, User.Identity!.Name!);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            ModelState.AddModelError("", "That POS ID or OPERA terminal ID is already used by another workstation.");
            return Page();
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return Page();
        }
        TempData["Message"] = "Workstation saved and logged.";
        return RedirectToPage(new { Edit = (long?)null });
    }
}
