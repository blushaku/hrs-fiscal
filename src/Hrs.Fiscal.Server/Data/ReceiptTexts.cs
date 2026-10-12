namespace Hrs.Fiscal.Server.Data;

/// <summary>Fixed receipt texts as ATK prescribes them (Albanian, Doc. 01-06-1967 Annex F).</summary>
public static class ReceiptTexts
{
    public static string TypeTitle(short couponType) => couponType switch
    {
        2 => "KUPON FISKAL I ANULUAR",
        3 => "KUPON FISKAL KTHIMI",
        _ => "KUPON FISKAL",
    };

    /// <summary>ATK payment type (1–6) as printed on the receipt ("Mënyra e pagesës").</summary>
    public static string PaymentLabel(int type) => type switch
    {
        1 => "KESH",
        2 => "KARTELË",
        3 => "VAUÇER",
        4 => "ÇEK",
        5 => "KRIPTOVALUTË",
        _ => "TJETËR",
    };
}
