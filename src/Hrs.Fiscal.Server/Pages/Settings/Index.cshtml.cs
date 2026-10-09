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
        [Required, RegularExpression("^[A-Z]{1,5}$", ErrorMessage = "Category: ATK code, 1–5 capital letters (e.g. TT, HT, UR).")] public string DefaultItemCategory { get; set; } = "TT";
        [Required, StringLength(10)] public string DefaultItemUnit { get; set; } = "cope";
        [RegularExpression("^[A-Za-z0-9-]{1,64}$", ErrorMessage = "Header name: letters, digits and '-' only.")] public string? FlipAuthHeader { get; set; } = FlipAuth.DefaultHeader;
        public bool ValidationCheckTaxNumber { get; set; } = true;
        [Range(0, 1, ErrorMessage = "Tolerance must be between 0.00 and 1.00 EUR.")] public decimal ValidationTolerance { get; set; } = 0.01m;
    }

    public PropertyClock Clock => clock;
    public FlipAuth.State Token { get; private set; } = new(false, FlipAuth.DefaultHeader, false, null, null);
    /// <summary>A just-generated token, shown once.</summary>
    public string? NewToken { get; private set; }

    [BindProperty] public GeneralSettings Input { get; set; } = new();

    /// <summary>Tiles on this page; each one is edited and saved on its own.</summary>
    public static readonly IReadOnlyDictionary<string, (string Title, string[] Fields)> Sections = new Dictionary<string, (string, string[])>
    {
        ["retention"] = ("Retention and backup", ["RetentionYears", "BackupTarget"]),
        ["atk"] = ("ATK connection", ["AtkEnvironment", "AtkApplicationId", "AtkTimeoutSeconds", "AtkRetryMinutes"]),
        ["alerts"] = ("Alerts", ["AlertEmails"]),
        ["vat"] = ("VAT calculation", ["VatRounding"]),
        ["signing"] = ("Signing", ["SigningModeDefault"]),
        ["overrides"] = ("OPERA overrides and receipt defaults", ["OperaOverridesEnabled", "DefaultItemCategory", "DefaultItemUnit"]),
        ["flip"] = ("OPERA / FLIP connection", ["FlipMode", "FlipStubStatus", "FlipStubContentType", "FlipStubBody"]),
        ["token"] = ("FLIP access token", ["FlipAuthRequired", "FlipAuthHeader"]),
        ["validation"] = ("Folio validation", ["ValidationCheckTaxNumber", "ValidationTolerance"]),
    };

    /// <summary>Field → setting key.</summary>
    private static readonly Dictionary<string, string> Keys = new()
    {
        ["RetentionYears"] = "retention_years", ["BackupTarget"] = "backup_target", ["AtkEnvironment"] = "atk_environment",
        ["AtkApplicationId"] = "atk_application_id", ["AtkTimeoutSeconds"] = "atk_timeout_seconds", ["AtkRetryMinutes"] = "atk_retry_minutes",
        ["AlertEmails"] = "alert_emails", ["VatRounding"] = "vat_rounding", ["SigningModeDefault"] = "signing_mode_default",
        ["OperaOverridesEnabled"] = "opera_overrides_enabled", ["DefaultItemCategory"] = "default_item_category", ["DefaultItemUnit"] = "default_item_unit",
        ["FlipMode"] = "flip_mode", ["FlipStubStatus"] = "flip_stub_status", ["FlipStubContentType"] = "flip_stub_content_type",
        ["FlipStubBody"] = "flip_stub_body", ["FlipAuthRequired"] = "flip_auth_required", ["FlipAuthHeader"] = "flip_auth_header",
        ["ValidationCheckTaxNumber"] = "validation_check_tax_number", ["ValidationTolerance"] = "validation_tolerance",
    };

    /// <summary>The tile being edited (null = all read-only).</summary>
    [BindProperty(SupportsGet = true)] public string? Edit { get; set; }

    /// <summary>Saved values, shown in the read-only tiles.</summary>
    public GeneralSettings Current { get; private set; } = new();

    public async Task OnGetAsync()
    {
        await LoadAsync();
        if (Edit is not null && !Sections.ContainsKey(Edit)) Edit = null;
        Input = Current;
    }

    private async Task LoadAsync()
    {
        Token = await flipAuth.StateAsync();
        NewToken = TempData["FlipToken"] as string;
        var all = await settings.AllAsync();
        int Int(string k, int d) => all.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : d;
        long Long(string k) => all.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        bool Bool(string k) => all.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.True;
        string Str(string k, string d) => all.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : d;
        decimal Dec(string k, decimal d) => all.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : d;
        bool BoolOr(string k, bool d) => all.TryGetValue(k, out var v) ? v.ValueKind == JsonValueKind.True : d;
        Current = new GeneralSettings
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
            DefaultItemCategory = Str("default_item_category", "TT"),
            DefaultItemUnit = Str("default_item_unit", "cope"),
            FlipAuthHeader = Str("flip_auth_header", FlipAuth.DefaultHeader),
            ValidationCheckTaxNumber = BoolOr("validation_check_tax_number", true),
            ValidationTolerance = Dec("validation_tolerance", 0.01m),
        };
    }

    /// <summary>Saves one tile only.</summary>
    public async Task<IActionResult> OnPostAsync(string section)
    {
        await LoadAsync();
        if (!Sections.TryGetValue(section ?? "", out var def)) return BadRequest();
        Edit = section;
        // Only the posted tile's fields are validated and saved; the others keep their stored values.
        foreach (var key in ModelState.Keys.ToList())
            if (key.StartsWith("Input.") && !def.Fields.Contains(key["Input.".Length..])) ModelState.Remove(key);
        if (section == "token" && Input.FlipAuthRequired && !Token.HasToken)
            ModelState.AddModelError("", "Generate a FLIP access token before requiring it.");
        if (!ModelState.IsValid)
        {
            // Keep the other editors filled with the stored values.
            foreach (var prop in typeof(GeneralSettings).GetProperties().Where(p => !def.Fields.Contains(p.Name)))
                prop.SetValue(Input, prop.GetValue(Current));
            return Page();
        }

        var values = new Dictionary<string, object?>
        {
            ["RetentionYears"] = Input.RetentionYears,
            ["BackupTarget"] = Input.BackupTarget?.Trim() ?? "",
            ["AtkEnvironment"] = Input.AtkEnvironment,
            ["AtkApplicationId"] = Input.AtkApplicationId,
            ["AtkTimeoutSeconds"] = Input.AtkTimeoutSeconds,
            ["AtkRetryMinutes"] = Input.AtkRetryMinutes,
            ["AlertEmails"] = Input.AlertEmails?.Trim() ?? "",
            ["VatRounding"] = Input.VatRounding,
            ["OperaOverridesEnabled"] = Input.OperaOverridesEnabled,
            ["SigningModeDefault"] = Input.SigningModeDefault,
            ["FlipMode"] = Input.FlipMode,
            ["FlipStubStatus"] = Input.FlipStubStatus,
            ["FlipStubContentType"] = string.IsNullOrWhiteSpace(Input.FlipStubContentType) ? "text/plain" : Input.FlipStubContentType.Trim(),
            ["FlipStubBody"] = Input.FlipStubBody ?? "",
            ["FlipAuthRequired"] = Input.FlipAuthRequired,
            ["DefaultItemCategory"] = (Input.DefaultItemCategory ?? "TT").Trim().ToUpperInvariant(),
            ["DefaultItemUnit"] = (Input.DefaultItemUnit ?? "cope").Trim(),
            ["FlipAuthHeader"] = FlipAuth.NormalizeHeader(Input.FlipAuthHeader),
            ["ValidationCheckTaxNumber"] = Input.ValidationCheckTaxNumber,
            ["ValidationTolerance"] = decimal.Round(Input.ValidationTolerance, 2),
        };
        var changed = await settings.SetManyAsync(def.Fields.ToDictionary(f => Keys[f], f => values[f]), User.Identity!.Name!);
        var message = changed == 0 ? $"{def.Title}: no changes." : $"{def.Title} saved and logged.";
        if (section == "signing")
        {
            var mustRegister = (await settings.TerminalsAsync()).Count(t => t.SigningState(Input.SigningModeDefault) == TerminalSigningState.RegisterAgain);
            if (mustRegister > 0) message += $" {mustRegister} workstation(s) must now be registered again under Workstations before they can sign.";
        }
        TempData["Message"] = message;
        return RedirectToPage(new { Edit = (string?)null });
    }

    /// <summary>Generates a new FLIP token (replacing the old one) and shows it once.</summary>
    public async Task<IActionResult> OnPostGenerateTokenAsync()
    {
        TempData["FlipToken"] = await flipAuth.GenerateAsync(User.Identity!.Name!);
        TempData["Message"] = "New FLIP access token created and required from now on. Copy it into FLIP now: it is shown only once.";
        return RedirectToPage();
    }
}
