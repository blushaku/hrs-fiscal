using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Hrs.Fiscal.Server.Data;
using Hrs.Fiscal.Server.Flip;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages.Settings;

public sealed class IndexModel(SettingsStore settings, FlipAuth flipAuth, PropertyClock clock) : PageModel
{
    public sealed class GeneralSettings
    {
        [Range(1, 50)] public int RetentionYears { get; set; } = 10;
        public string? BackupTarget { get; set; }
        [RegularExpression("Test|Production")] public string AtkEnvironment { get; set; } = "Test";
        [Range(0, long.MaxValue)] public long AtkApplicationId { get; set; }
        [Range(2, 60)] public int AtkTimeoutSeconds { get; set; } = 10;
        [Range(1, 60)] public int AtkRetryMinutes { get; set; } = 2;
        public string? AlertEmails { get; set; }
        [RegularExpression("RoundTaxHalfUp|TruncateNet")] public string VatRounding { get; set; } = "RoundTaxHalfUp";
        public bool OperaOverridesEnabled { get; set; }
        [RegularExpression("client|server")] public string SigningModeDefault { get; set; } = "client";
        [RegularExpression("capture|live")] public string FlipMode { get; set; } = "capture";
        [Range(100, 599)] public int FlipStubStatus { get; set; } = 200;
        public string? FlipStubContentType { get; set; } = "text/plain";
        public string? FlipStubBody { get; set; }
        public bool FlipAuthRequired { get; set; }
        [RegularExpression("^[A-Za-z0-9-]{1,64}$", ErrorMessage = "Header name: letters, digits and '-' only.")] public string? FlipAuthHeader { get; set; } = FlipAuth.DefaultHeader;
    }

    public PropertyClock Clock => clock;
    public FlipAuth.State Token { get; private set; } = new(false, FlipAuth.DefaultHeader, false, null, null);
    /// <summary>A just-generated token, shown once.</summary>
    public string? NewToken { get; private set; }

    [BindProperty] public GeneralSettings Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        Token = await flipAuth.StateAsync();
        NewToken = TempData["FlipToken"] as string;
        var all = await settings.AllAsync();
        int Int(string k, int d) => all.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : d;
        long Long(string k) => all.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        bool Bool(string k) => all.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.True;
        string Str(string k, string d) => all.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : d;
        Input = new GeneralSettings
        {
            RetentionYears = Int("retention_years", 10),
            BackupTarget = Str("backup_target", ""),
            AtkEnvironment = Str("atk_environment", "Test"),
            AtkApplicationId = Long("atk_application_id"),
            AtkTimeoutSeconds = Int("atk_timeout_seconds", 10),
            AtkRetryMinutes = Int("atk_retry_minutes", 2),
            AlertEmails = Str("alert_emails", ""),
            VatRounding = Str("vat_rounding", "RoundTaxHalfUp"),
            OperaOverridesEnabled = Bool("opera_overrides_enabled"),
            SigningModeDefault = Str("signing_mode_default", "client"),
            FlipMode = Str("flip_mode", "capture"),
            FlipStubStatus = Int("flip_stub_status", 200),
            FlipStubContentType = Str("flip_stub_content_type", "text/plain"),
            FlipStubBody = Str("flip_stub_body", ""),
            FlipAuthRequired = Bool("flip_auth_required"),
            FlipAuthHeader = Str("flip_auth_header", FlipAuth.DefaultHeader),
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Token = await flipAuth.StateAsync();
        if (Input.FlipAuthRequired && !Token.HasToken)
            ModelState.AddModelError("", "Generate a FLIP access token before requiring it.");
        if (!ModelState.IsValid) return Page();
        var changed = await settings.SetManyAsync(new Dictionary<string, object?>
        {
            ["retention_years"] = Input.RetentionYears,
            ["backup_target"] = Input.BackupTarget ?? "",
            ["atk_environment"] = Input.AtkEnvironment,
            ["atk_application_id"] = Input.AtkApplicationId,
            ["atk_timeout_seconds"] = Input.AtkTimeoutSeconds,
            ["atk_retry_minutes"] = Input.AtkRetryMinutes,
            ["alert_emails"] = Input.AlertEmails ?? "",
            ["vat_rounding"] = Input.VatRounding,
            ["opera_overrides_enabled"] = Input.OperaOverridesEnabled,
            ["signing_mode_default"] = Input.SigningModeDefault,
            ["flip_mode"] = Input.FlipMode,
            ["flip_stub_status"] = Input.FlipStubStatus,
            ["flip_stub_content_type"] = string.IsNullOrWhiteSpace(Input.FlipStubContentType) ? "text/plain" : Input.FlipStubContentType.Trim(),
            ["flip_stub_body"] = Input.FlipStubBody ?? "",
            ["flip_auth_required"] = Input.FlipAuthRequired,
            ["flip_auth_header"] = FlipAuth.NormalizeHeader(Input.FlipAuthHeader),
        }, User.Identity!.Name!);
        var mustRegister = (await settings.TerminalsAsync())
            .Count(t => t.SigningState(Input.SigningModeDefault) == TerminalSigningState.RegisterAgain);
        TempData["Message"] = (changed == 0 ? "No changes." : $"{changed} setting(s) saved and logged.")
            + (mustRegister > 0 ? $" {mustRegister} workstation(s) must now be registered again under Workstations before they can sign." : "");
        return RedirectToPage();
    }

    /// <summary>Generates a new FLIP token (replacing the old one) and shows it once.</summary>
    public async Task<IActionResult> OnPostGenerateTokenAsync()
    {
        TempData["FlipToken"] = await flipAuth.GenerateAsync(User.Identity!.Name!);
        TempData["Message"] = "New FLIP access token created and required from now on. Copy it into FLIP now: it is shown only once.";
        return RedirectToPage();
    }
}
