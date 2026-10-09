using System.Globalization;
using System.Text.Json.Nodes;

namespace Hrs.Fiscal.Server.Flip;

/// <summary>A charge line of an OPERA folio, VAT-inclusive, in the folio's local currency.</summary>
public sealed record OperaLine(string TrxCode, string Description, decimal Quantity, decimal Gross, decimal VatPercent, string? Group);

/// <summary>A payment on the folio (positive = paid by the guest; negative = paid out / refund).</summary>
public sealed record OperaPayment(string TrxCode, string Description, decimal Amount);

/// <summary>The fiscally relevant content of one OPERA Cloud fiscal payload (OFIS JSON, as delivered by FLIP).</summary>
public sealed record OperaFolio
{
    public required string FiscalFolioId { get; init; }
    public required string TerminalId { get; init; }
    public required string Command { get; init; }
    public string HotelCode { get; init; } = "";
    public string LocalCurrency { get; init; } = "";
    public string CountryCode { get; init; } = "";
    public string? Operator { get; init; }
    public string? FolioReference { get; init; }            // reservation / room, for the archive
    public string? AssociatedFiscalBillNo { get; init; }    // original receipt for credit folios
    public required IReadOnlyList<OperaLine> Lines { get; init; }
    public required IReadOnlyList<OperaPayment> Payments { get; init; }
    public decimal Gross => Lines.Sum(l => l.Gross);
    public decimal Paid => Payments.Sum(p => p.Amount);
}

/// <summary>
/// Reads OPERA Cloud's fiscal payload (DocumentInfo / FolioInfo / Postings / TrxInfo …) as sent by on-premise FLIP.
/// Rules (from captured OPERA Cloud 26.4 payloads, pending Oracle's specification):
///   • TrxInfo.TrxCodeType "X" = tax postings: not receipt lines; their TaxRate gives the parent line's VAT %.
///   • TrxType "FC" = payments (GuestAccountCredit − GuestAccountDebit).
///   • Other postings are charges: GrossAmount (TaxInclusive) or NetAmount + generated taxes (tax-exclusive).
/// </summary>
public static class OperaPayload
{
    public static bool LooksLikeOperaFolio(byte[] body)
    {
        var head = System.Text.Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 512));
        return head.Contains("\"DocumentInfo\"", StringComparison.Ordinal);
    }

    public static OperaFolio Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("The FLIP message is not a JSON object.");
        var doc = root["DocumentInfo"] as JsonObject ?? throw new FormatException("DocumentInfo is missing: not an OPERA fiscal payload.");
        var folio = root["FolioInfo"] as JsonObject ?? throw new FormatException("FolioInfo is missing.");

        var trxInfo = new Dictionary<string, (string Description, string Type, string TrxType, string? Group)>(StringComparer.Ordinal);
        foreach (var t in Array(folio["TrxInfo"]))
            if (Str(t, "Code") is { Length: > 0 } code)
                trxInfo[code] = (Str(t, "Description") ?? code, Str(t, "TrxCodeType") ?? "", Str(t, "TrxType") ?? "", Str(t, "Group"));

        bool IsTax(string code) => trxInfo.TryGetValue(code, out var i) && i.Type == "X";

        var lines = new List<OperaLine>();
        var payments = new List<OperaPayment>();
        foreach (var p in Array(folio["Postings"]))
        {
            var code = Str(p, "TrxCode") ?? "";
            var trxType = Str(p, "TrxType") ?? (trxInfo.TryGetValue(code, out var ti) ? ti.TrxType : "");
            var description = trxInfo.TryGetValue(code, out var info) ? info.Description : code;
            if (IsTax(code)) continue; // included in (or added to) the parent charge

            if (trxType == "FC")
            {
                var amount = Dec(p, "GuestAccountCredit") - Dec(p, "GuestAccountDebit");
                if (amount == 0) amount = Dec(p, "UnitPrice") * Qty(p);
                if (amount != 0) payments.Add(new OperaPayment(code, description, amount));
                continue;
            }

            var taxes = Array((p as JsonObject)?["Generates"]?["Generate"]).Where(g => IsTax(Str(g, "TrxCode") ?? "")).ToList();
            var vat = taxes.Sum(g => Dec(g, "TaxRate"));
            decimal gross;
            if (Bool(p, "TaxInclusive") || taxes.Count == 0)
                gross = Has(p, "GrossAmount") ? Dec(p, "GrossAmount") : Dec(p, "GuestAccountDebit") - Dec(p, "GuestAccountCredit");
            else
                gross = Dec(p, "NetAmount") + taxes.Sum(g => Has(g, "GrossAmount") ? Dec(g, "GrossAmount") : Dec(g, "NetAmount"));
            if (gross == 0) continue;
            lines.Add(new OperaLine(code, description, Qty(p), decimal.Round(gross, 2, MidpointRounding.AwayFromZero), vat, info.Group));
        }

        var udfs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var block in Array(root["UserDefinedFields"]?["CharacterUDFs"]))
            foreach (var u in Array(block?["UDF"]))
                if (Str(u, "Name") is { } n && Str(u, "Value") is { Length: > 0 } v) udfs[n] = v;

        var resv = root["ReservationInfo"] as JsonObject;
        var user = root["FiscalFolioUserInfo"] as JsonObject;
        var appUser = Str(user, "AppUser");
        return new OperaFolio
        {
            FiscalFolioId = Str(doc, "FiscalFolioId") is { Length: > 0 } id ? id : throw new FormatException("DocumentInfo.FiscalFolioId is missing."),
            TerminalId = Str(doc, "TerminalId") ?? "",
            Command = Str(doc, "Command") ?? "",
            HotelCode = Str(doc, "HotelCode") ?? "",
            CountryCode = Str(doc, "CountryCode") ?? "",
            LocalCurrency = Str(root["HotelInfo"], "LocalCurrency") ?? "",
            Operator = appUser is { Length: > 0 } ? appUser.Split('@')[0] : Str(Array(folio["Postings"]).FirstOrDefault(), "CashierId"),
            FolioReference = resv is null ? null : $"Folio {Str(doc, "FiscalFolioId")} · conf. {Str(resv, "ConfirmationNo")} · room {Str(resv, "RoomNumber")}",
            AssociatedFiscalBillNo = udfs.GetValueOrDefault("FLIP_ASSOCIATED_FISCAL_BILL_NO"),
            Lines = lines,
            Payments = payments,
        };
    }

    private static IEnumerable<JsonNode?> Array(JsonNode? n) => n is JsonArray a ? a : [];

    private static bool Has(JsonNode? n, string key) => n is JsonObject o && o[key] is not null;

    private static string? Str(JsonNode? n, string key) =>
        n is JsonObject o && o[key] is JsonValue v ? v.ToString() : null;

    private static bool Bool(JsonNode? n, string key) =>
        n is JsonObject o && o[key] is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : string.Equals(v.ToString(), "true", StringComparison.OrdinalIgnoreCase));

    private static decimal Dec(JsonNode? n, string key)
    {
        if (n is not JsonObject o || o[key] is not JsonValue v) return 0;
        if (v.TryGetValue<decimal>(out var d)) return d;
        if (v.TryGetValue<double>(out var dbl)) return (decimal)dbl;
        return decimal.TryParse(v.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : 0;
    }

    private static decimal Qty(JsonNode? p) => Dec(p, "Quantity") is > 0 and var q ? q : 1;
}
