using System.Globalization;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Server.Data;

namespace Hrs.Fiscal.Server.Printing;

/// <summary>
/// The whole fiscal receipt as an ESC/POS print job, laid out like ATK's SEF receipt samples (Doc. 01-06-1967 Art 25.18,
/// Annex F) and like the PDF copy: taxpayer and unit, items, totals, payments, VAT groups, receipt number, date, SEF ID,
/// NUIKF, daily number, OFFLINE when it applies, QR, "e-kupon" and the fiscal logo. A copy carries "KOPJE E KUPONIT".
/// </summary>
public static class ThermalReceipt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static byte[] Build(ReceiptDetail r, PosCoupon coupon, IReadOnlyDictionary<string, decimal> rates, PropertyClock clock,
        int paperMm, bool copy, DateTime? referenceAt = null)
    {
        string Eur(long cents) => (cents / 100m).ToString("0.00", Inv);
        string Price(long units) => (units / 10000m).ToString("0.00##", Inv);
        var p = new EscPos(paperMm);

        // Taxpayer and unit
        p.Align(TextAlign.Center).Bold(true).Line(r.BusinessName).Bold(false);
        if (!string.IsNullOrWhiteSpace(r.BranchName) && r.BranchName != r.BusinessName) p.Line(r.BranchName);
        if (!string.IsNullOrWhiteSpace(r.Address)) p.Line(r.Address);
        if (!string.IsNullOrWhiteSpace(r.Location)) p.Line(r.Location);
        p.Line($"NF-NUI: {r.BusinessNui.ToString(Inv)}");
        if (!string.IsNullOrWhiteSpace(r.VatNo)) p.Line($"NUMRI I TVSH-SË: {r.VatNo}");
        p.Align(TextAlign.Left).Line($"EMRI I PUNËTORIT: {r.OperatorId}");

        if (copy)
        {
            p.Rule('=').Align(TextAlign.Center).Bold(true).Large(true).Line("KOPJE E KUPONIT", p.Columns / 2).Large(false).Bold(false).Align(TextAlign.Left).Rule('=');
        }
        p.Align(TextAlign.Center).Bold(true).Line(ReceiptTexts.TypeTitle(r.CouponType)).Bold(false).Align(TextAlign.Left);
        if (r.ReferenceCouponId is { } reference)
            p.Line($"KUPON FISKAL NR. {reference}" + (referenceAt is { } at ? $" DATË: {clock.ToLocal(at):dd/MM/yyyy HH:mm}" : ""));
        if (r.CancelReason is { } reason) p.Bold(true).Line($"ARSYEJA E ANULIMIT: {reason.ToUpperInvariant()}").Bold(false);
        if (r.PaperSerial is { } serial) p.Bold(true).Line($"NGA BLLOKU I VEÇANTË, NR. {serial}").Bold(false);

        // Items: name on its own line, then quantity × price, amount and VAT letter.
        p.Rule().LeftRight("ARTIKULLI / SASIA X ÇMIMI", "SHUMA").Rule();
        foreach (var i in coupon.Items)
        {
            var gross = (long)Math.Round(i.Price * (decimal)i.Quantity, MidpointRounding.AwayFromZero);
            p.Line(i.Name);
            p.LeftRight($"  {((decimal)i.Quantity).ToString("0.###", Inv)} X {Price(i.Price)}", $"{(gross / 10000m).ToString("0.00", Inv)} {i.TaxRate}");
            if (gross != i.Total) p.LeftRight("  Ulje", "-" + ((gross - i.Total) / 10000m).ToString("0.00", Inv) + "  ");
        }
        p.Rule();

        // Totals, payments, VAT
        if (coupon.TotalDiscount > 0) p.LeftRight("ULJE GJITHSEJ", "-" + Eur(coupon.TotalDiscount));
        p.Bold(true).LeftRight("TOTALI PËR PAGESË", Eur(coupon.Total) + " EUR").Bold(false);
        foreach (var pay in coupon.Payments) p.LeftRight($"MËNYRA E PAGESËS {ReceiptTexts.PaymentLabel((int)pay.Type)}", Eur(pay.Amount));
        foreach (var g in coupon.TaxGroups)
            p.LeftRight($"TVSH {g.TaxRate}" + (rates.TryGetValue(g.TaxRate, out var pct) ? $" {pct.ToString("0.##", Inv)}%" : ""), Eur(g.TotalTax));
        p.LeftRight("TOTAL PA TVSH", Eur(coupon.TotalNoTax));
        p.Line($"ARTIKUJ - {coupon.Items.Count}");
        p.Rule();

        // Fiscal identification
        p.Line(r.CouponId.ToString("0000000000", Inv));
        p.Line($"DATA DHE ORA {clock.ToLocal(r.IssuedAt):dd/MM/yyyy HH:mm}");
        p.Line($"NR. IDENTIFIKUES I SEF {r.BranchId}-{r.BusinessNui}-{r.PosId}");
        p.Bold(true).Line($"NUIKF: {r.VerificationNo}").Bold(false);
        if (r.DailyNo is { } daily) p.Line($"KUPON FISKAL DITOR NR. {daily:0000}");
        if (r.Offline) p.Align(TextAlign.Center).Bold(true).Large(true).Line("OFFLINE", p.Columns / 2).Large(false).Bold(false).Align(TextAlign.Left);

        p.Feed().Align(TextAlign.Center).Qr(r.QrString).Feed();
        p.Bold(true).Line("e-kupon").Bold(false);
        // Fiscal logo (text form until ATK's artwork is supplied as an image for the printer's logo memory).
        p.Bold(true).Large(true).Line("RKS", p.Columns / 2).Large(false).Line("MF").Bold(false);
        p.Line($"Transaksioni ATK: {r.AtkTransactionId?.ToString("0", Inv) ?? "në pritje"}");
        if (copy) p.Line($"Kopje e printuar {clock.Format(DateTime.UtcNow)}");
        return p.Align(TextAlign.Left).Feed(3).Cut().ToArray();
    }

    /// <summary>A short test page: shows that the printer is reached, the paper width and the Albanian letters.</summary>
    public static byte[] TestPage(string workstation, string printer, int paperMm, PropertyClock clock)
    {
        var p = new EscPos(paperMm);
        p.Align(TextAlign.Center).Bold(true).Large(true).Line("PROVË", p.Columns / 2).Large(false).Line("Test i printerit").Bold(false);
        p.Line("Ky nuk është kupon fiskal");
        p.Align(TextAlign.Left).Rule();
        p.Line($"Vendpunësi: {workstation}");
        p.Line($"Printeri: {printer}");
        p.Line($"Letra: {paperMm} mm, {p.Columns} shkronja për rresht");
        p.Line($"Data: {clock.Format(DateTime.UtcNow)}");
        p.Line("Shkronjat: ë Ë ç Ç");
        p.Line(string.Concat(Enumerable.Range(0, p.Columns).Select(i => (char)('0' + (i + 1) % 10))));
        p.Rule().Align(TextAlign.Center).Qr("TEST").Feed();
        return p.Line("e-kupon").Align(TextAlign.Left).Feed(3).Cut().ToArray();
    }
}
