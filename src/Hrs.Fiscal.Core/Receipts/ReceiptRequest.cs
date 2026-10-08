using Hrs.Fiscal.Core.Atk;

namespace Hrs.Fiscal.Core.Receipts;

/// <summary>
/// Source-system-neutral description of a sale to be fiscalized.
/// Connectors (OFIS, POS) map their own payloads to this type; the
/// <see cref="CouponBuilder"/> turns it into ATK's protobuf PosCoupon.
/// Amounts are VAT-inclusive euros.
/// </summary>
public sealed record ReceiptRequest
{
    public required ulong BusinessId { get; init; }        // NUI
    public required ulong BranchId { get; init; }          // unit / branch number registered with ATK
    public required ulong PosId { get; init; }             // unique per branch; one per workstation
    public required ulong ApplicationId { get; init; }     // issued by ATK on SEF certification
    public required ulong CouponId { get; init; }          // unique across the whole business
    public required string VerificationNo { get; init; }   // NUIKF, max 16 chars
    public required string Location { get; init; }
    public required string OperatorId { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public CouponType Type { get; init; } = CouponType.Sale;

    /// <summary>CouponId of the original sale. Required for Return (and Cancel, which HRS does not use).</summary>
    public ulong ReferenceNo { get; init; }

    public required IReadOnlyList<ReceiptLine> Lines { get; init; }
    public required IReadOnlyList<ReceiptPayment> Payments { get; init; }
}

public sealed record ReceiptLine
{
    public required string Name { get; init; }
    public required string Unit { get; init; }             // e.g. "cope", "nate" (night)
    public required decimal Quantity { get; init; }
    public required decimal UnitPrice { get; init; }       // VAT-inclusive, up to 4 decimals
    public required string TaxRate { get; init; }          // A, C, D, E
    public required string Category { get; init; }         // ATK goods/service category code, e.g. "HT"
    public decimal Discount { get; init; }                 // VAT-inclusive euros off this line
}

public sealed record ReceiptPayment(PaymentType Type, decimal Amount);
