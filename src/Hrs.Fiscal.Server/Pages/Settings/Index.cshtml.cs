using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Hrs.Fiscal.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hrs.Fiscal.Server.Pages.Settings;

public sealed class IndexModel(SettingsStore settings) : PageModel
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
        [RegularExpression("capture|live")] public string FlipMode { get; set; } = "capture";
        [Range(100, 599)] public int FlipStubStatus { get; set; } = 200;
        public string? FlipStubContentType { get; set; } = "text/plain";
        public string? FlipStubBody { get; set; }
    }

    [BindProperty] public GeneralSettings Input { get; set; } = new();

    public async Task OnGetAsync()
    {
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
            FlipMode = Str("flip_mode", "capture"),
            FlipStubStatus = Int("flip_stub_status", 200),
            FlipStubContentType = Str("flip_stub_content_type", "text/plain"),
            FlipStubBody = Str("flip_stub_body", ""),
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
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
            ["flip_mode"] = Input.FlipMode,
            ["flip_stub_status"] = Input.FlipStubStatus,
            ["flip_stub_content_type"] = string.IsNullOrWhiteSpace(Input.FlipStubContentType) ? "text/plain" : Input.FlipStubContentType.Trim(),
            ["flip_stub_body"] = Input.FlipStubBody ?? "",
        }, User.Identity!.Name!);
        TempData["Message"] = changed == 0 ? "No changes." : $"{changed} setting(s) saved and logged.";
        return RedirectToPage();
    }
}
