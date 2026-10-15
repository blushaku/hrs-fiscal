using Dapper;
using Google.Protobuf;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Core.Signing;
using Npgsql;

namespace Hrs.Fiscal.Server.Data;

/// <summary>Appends signed receipts and their transmission attempts to the archive.</summary>
/// <param name="CancelReason">Reason printed on a Cancel receipt.</param>
/// <param name="PaperSerial">Serial number of a receipt written from the special paper block.</param>
public sealed record ReceiptExtras(string? CancelReason = null, string? PaperSerial = null);

public sealed class ReceiptArchive(NpgsqlDataSource db, PropertyClock clock, Hrs.Fiscal.Server.Services.AtkStatus atkStatus)
{
    public async Task<long> NextCouponIdAsync(long branchId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>("SELECT fiscal.next_coupon_id(@branchId)", new { branchId });
    }

    public async Task<long> AppendSourcePayloadAsync(string source, string eventId, string contentType, string body, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO fiscal.source_payload (source, source_event_id, content_type, body)
            VALUES (@source, @eventId, @contentType, @body) RETURNING id
            """, new { source, eventId, contentType, body });
    }

    public async Task<long> AppendReceiptAsync(PosCoupon coupon, SignedPayload signedPos, string qrString, long terminalId,
        bool issuedOffline, long? sourcePayloadId, string? sourceDocument, CancellationToken ct = default, ReceiptExtras? extras = null)
    {
        // Daily number: per workstation and property day of issue; assigned by the chain trigger.
        var day = DateOnly.FromDateTime(clock.ToLocal(DateTimeOffset.FromUnixTimeSeconds((long)coupon.Time).UtcDateTime));
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO fiscal.receipt (coupon_id, verification_no, coupon_type, reference_coupon_id, business_nui, branch_id, pos_id,
                terminal_id, application_id, operator_id, source_payload_id, source_document, issued_at, issued_offline,
                total_cents, total_tax_cents, pos_coupon, signature, qr_string, issued_day, cancel_reason, paper_serial)
            VALUES (@couponId, @verificationNo, @type, @reference, @nui, @branch, @pos, @terminalId, @app, @operatorId,
                @sourcePayloadId, @sourceDocument, to_timestamp(@time), @issuedOffline, @total, @tax, @bytes, @signature, @qrString,
                @day, @cancelReason, @paperSerial)
            RETURNING id
            """, new
        {
            couponId = (long)coupon.CouponId,
            verificationNo = coupon.VerificationNo,
            type = (short)coupon.Type,
            reference = coupon.ReferenceNo == 0 ? (long?)null : (long)coupon.ReferenceNo,
            nui = (long)coupon.BusinessId,
            branch = (long)coupon.BranchId,
            pos = (long)coupon.PosId,
            terminalId,
            app = (long)coupon.ApplicationId,
            operatorId = coupon.OperatorId,
            sourcePayloadId,
            sourceDocument,
            time = (double)coupon.Time,
            issuedOffline,
            total = coupon.Total,
            tax = coupon.TotalTax,
            // The archived bytes must be exactly the bytes that were signed.
            bytes = Convert.FromBase64String(signedPos.Details),
            signature = signedPos.Signature,
            qrString,
            day = day.ToDateTime(TimeOnly.MinValue),
            cancelReason = extras?.CancelReason,
            paperSerial = extras?.PaperSerial,
        });
    }

    public async Task AppendTransmissionAsync(long receiptId, string sentBy, AtkSendResult result, DateTime? at = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO fiscal.transmission (receipt_id, attempted_at, sent_by, outcome, http_status, atk_transaction_id, message)
            VALUES (@receiptId, coalesce(@at, now()), @sentBy, @outcome, @status, @tx, @message)
            """, new
        {
            receiptId,
            at,
            sentBy,
            outcome = result.Outcome.ToString().ToLowerInvariant(),
            status = result.HttpStatus,
            tx = result.TransactionId is { } t ? (decimal?)t : null,
            message = result.Message,
        });
        // ATK answered (accepted or rejected) = reachable; no answer, timeout or 5xx = offline.
        atkStatus.Record(result.Outcome != AtkOutcome.Transient, sentBy.Contains("queue") ? "queue" : "send", result.Message, at);
    }
}
