using System.ComponentModel.DataAnnotations;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Signing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Hrs.Fiscal.Server.Pages.Settings;

public sealed class WorkstationsModel(SettingsStore settings, PropertyClock clock, TerminalEnrollment enrollment, IServerKeyStore keyStore,
    ILogger<WorkstationsModel> log) : PageModel
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
        /// <summary>"" = property default, "client" or "server".</summary>
        [RegularExpression("client|server")] public string? SigningMode { get; set; }
    }

    [BindProperty] public Form Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public long? Edit { get; set; }
    public IReadOnlyList<TerminalEdit> Terminals { get; private set; } = [];
    public PropertyClock Clock => clock;
    public string DefaultMode { get; private set; } = SigningModes.Client;
    public string AtkEnvironment { get; private set; } = "Test";
    public string KeyStoreKind => keyStore.Kind;
    public TerminalEdit? Current => Terminals.SingleOrDefault(t => t.Id == Input.Id && Input.Id != 0);

    private async Task LoadAsync()
    {
        Terminals = await settings.TerminalsAsync();
        DefaultMode = await settings.SigningModeDefaultAsync();
        AtkEnvironment = await settings.GetAsync("atk_environment", "Test") ?? "Test";
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();
        if (Edit is { } id && Terminals.SingleOrDefault(t => t.Id == id) is { } t)
            Input = new Form { Id = t.Id, PosId = t.PosId, OperaTerminalId = t.OperaTerminalId, Hostname = t.Hostname, ClientEndpoint = t.ClientEndpoint, Description = t.Description, Status = t.Status, SigningMode = t.SigningMode ?? "" };
        else
            Input = new Form { PosId = Terminals.Count == 0 ? 1 : Terminals.Max(t => t.PosId) + 1 };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();
        if (!ModelState.IsValid) return Page();
        try
        {
            await settings.SaveTerminalAsync(new TerminalEdit
            {
                Id = Input.Id, PosId = Input.PosId, OperaTerminalId = Input.OperaTerminalId?.Trim(), Hostname = Input.Hostname.Trim(),
                ClientEndpoint = Input.ClientEndpoint?.Trim(), Description = Input.Description?.Trim(), Status = Input.Status,
                SigningMode = string.IsNullOrEmpty(Input.SigningMode) ? null : Input.SigningMode,
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

    /// <summary>Central mode: generate a key on this server and get the workstation's certificate from ATK.</summary>
    public async Task<IActionResult> OnPostRegisterAsync(long id)
    {
        try
        {
            var r = await enrollment.EnrollAsync(id, User.Identity!.Name!);
            TempData["Message"] = $"Registered with ATK ({r.Environment}) as {r.BusinessName}. Certificate valid until " +
                                  $"{clock.ToLocal(r.CertificateExpiresUtc):dd.MM.yyyy}; key kept on this server ({(r.KeyStoreKind == "cng" ? "Windows key store, non-exportable" : "file key store, test only")}).";
        }
        catch (Exception ex) when (ex is InvalidOperationException or AtkApiException or HttpRequestException or TaskCanceledException
                                       or System.Security.Cryptography.CryptographicException)
        {
            log.LogWarning(ex, "ATK registration failed for terminal {Id}", id);
            TempData["Error"] = "Registration failed: " + (ex is TaskCanceledException ? "ATK did not answer in time." : ex.Message);
        }
        return RedirectToPage(new { Edit = id });
    }

    /// <summary>Central mode: take over a key and certificate exported from ATK's onboarder tool.</summary>
    public async Task<IActionResult> OnPostImportAsync(long id, IFormFile? key, IFormFile? cert)
    {
        try
        {
            if (key is null || cert is null) throw new InvalidOperationException("Choose both files: private key and certificate (PEM).");
            if (key.Length > 64_000 || cert.Length > 64_000) throw new InvalidOperationException("These files are too large to be a PEM key and certificate.");
            using var kr = new StreamReader(key.OpenReadStream());
            using var cr = new StreamReader(cert.OpenReadStream());
            var r = await enrollment.ImportAsync(id, await kr.ReadToEndAsync(), await cr.ReadToEndAsync(), User.Identity!.Name!);
            TempData["Message"] = $"Certificate imported for {r.BusinessName}, valid until {clock.ToLocal(r.CertificateExpiresUtc):dd.MM.yyyy}. Delete the exported key file from your computer.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            TempData["Error"] = "Import failed: " + ex.Message;
        }
        return RedirectToPage(new { Edit = id });
    }
}
