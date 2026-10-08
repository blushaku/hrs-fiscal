namespace Hrs.Fiscal.Server.Data;

public static class CouponTypes
{
    public const short Sale = 1, Cancel = 2, Return = 3;

    public static string Label(short type) => type switch
    {
        Sale => "Sale",
        Cancel => "Cancel",
        Return => "Return",
        _ => "Unknown",
    };
}

public sealed record ReceiptFilter
{
    public string? Query { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public long? TerminalId { get; init; }
    public string? Status { get; init; }      // accepted | pending | rejected
    public short? Type { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public sealed class ReceiptListRow
{
    public long Id { get; init; }
    public long CouponId { get; init; }
    public string VerificationNo { get; init; } = "";
    public short CouponType { get; init; }
    public long? ReferenceCouponId { get; init; }
    public string? SourceDocument { get; init; }
    public string OperatorId { get; init; } = "";
    public string TerminalLabel { get; init; } = "";
    public long PosId { get; init; }
    public DateTime IssuedAt { get; init; }
    public bool IssuedOffline { get; init; }
    public long TotalCents { get; init; }
    public string Status { get; init; } = "";
    public decimal? AtkTransactionId { get; init; }
}

public sealed class ReceiptDetail
{
    public long Id { get; init; }
    public long CouponId { get; init; }
    public string VerificationNo { get; init; } = "";
    public short CouponType { get; init; }
    public long? ReferenceCouponId { get; init; }
    public long BusinessNui { get; init; }
    public long BranchId { get; init; }
    public long PosId { get; init; }
    public long TerminalId { get; init; }
    public string TerminalLabel { get; init; } = "";
    public long ApplicationId { get; init; }
    public string OperatorId { get; init; } = "";
    public string? SourceDocument { get; init; }
    public DateTime IssuedAt { get; init; }
    public bool IssuedOffline { get; init; }
    public long TotalCents { get; init; }
    public long TotalTaxCents { get; init; }
    public byte[] PosCoupon { get; init; } = [];
    public string Signature { get; init; } = "";
    public string QrString { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public byte[]? RowHash { get; init; }
    public string Status { get; init; } = "";
    public decimal? AtkTransactionId { get; init; }
    public string BusinessName { get; init; } = "";
    public string? VatNo { get; init; }
    public string BranchName { get; init; } = "";
    public string Location { get; init; } = "";
    public string? Address { get; init; }
}

public sealed class TransmissionRow
{
    public DateTime AttemptedAt { get; init; }
    public string SentBy { get; init; } = "";
    public string Outcome { get; init; } = "";
    public int? HttpStatus { get; init; }
    public decimal? AtkTransactionId { get; init; }
    public string? Message { get; init; }
}

public sealed class SourcePayloadRow
{
    public string Source { get; init; } = "";
    public string SourceEventId { get; init; } = "";
    public string ContentType { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTime ReceivedAt { get; init; }
}

public sealed class AuditRow
{
    public long Id { get; init; }
    public DateTime At { get; init; }
    public string Actor { get; init; } = "";
    public string? TerminalLabel { get; init; }
    public string Action { get; init; } = "";
    public string? Entity { get; init; }
    public string? EntityId { get; init; }
    public string Details { get; init; } = "{}";
}

public sealed record AuditFilter
{
    public string? Query { get; init; }
    public string? Action { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 100;
}

public sealed class TerminalRow
{
    public long Id { get; init; }
    public long PosId { get; init; }
    public string? OperaTerminalId { get; init; }
    public string Hostname { get; init; } = "";
    public string Status { get; init; } = "";
    public DateTime? CertificateExpires { get; init; }
    public DateTime? LastReceiptAt { get; init; }
    public long PendingCount { get; init; }
    public string Label => string.IsNullOrEmpty(OperaTerminalId) ? Hostname : $"{OperaTerminalId} · {Hostname}";
}

public sealed class DashboardStats
{
    public long IssuedToday { get; init; }
    public long TotalTodayCents { get; init; }
    public long AcceptedToday { get; init; }
    public long Pending { get; init; }
    public DateTime? OldestPendingIssuedAt { get; init; }
    public long Overdue48h { get; init; }
    public long RejectedToday { get; init; }
    public long Rejected30Days { get; init; }
}

public sealed class AppUser
{
    public long Id { get; init; }
    public string Username { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Role { get; init; } = "";
    public string PasswordHash { get; init; } = "";
    public bool Active { get; init; }
}

public sealed record Paged<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize)
{
    public int PageCount => (int)Math.Max(1, (Total + PageSize - 1) / PageSize);
}
