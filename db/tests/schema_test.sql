-- Schema self-test. Run against an EMPTY database after V001:
--   psql -v ON_ERROR_STOP=1 -f db/tests/schema_test.sql
-- Prints PASS lines; any failure aborts with an error.
SET search_path = fiscal;
\set QUIET on

INSERT INTO business (nui, name, fiscalization_no) VALUES (812345678, 'Hotel Test SH.P.K.', 'F-001');
INSERT INTO branch (business_nui, branch_id, name, location) VALUES (812345678, 5130484, 'Hotel Test', 'Prishtine');
INSERT INTO terminal (business_nui, branch_id, pos_id, opera_terminal_id, hostname, status)
VALUES (812345678, 5130484, 11, 'FO1', 'FRONTDESK-01', 'active');

DO $$
DECLARE
    c1 bigint := fiscal.next_coupon_id(5130484);
    c2 bigint := fiscal.next_coupon_id(5130484);
    ok boolean;
BEGIN
    ASSERT c1 = 51304840000000001 AND c2 = c1 + 1, 'coupon id = branch * 10^10 + seq';
    RAISE NOTICE 'PASS coupon ids % %', c1, c2;

    INSERT INTO fiscal.receipt (coupon_id, verification_no, coupon_type, business_nui, branch_id, pos_id, terminal_id,
        application_id, operator_id, issued_at, total_cents, total_tax_cents, pos_coupon, signature, qr_string)
    VALUES (c1, 'ABCDEFGH23456789', 1, 812345678, 5130484, 11, 1, 1234, 'anna', now() - interval '3 days', 25000, 1852, '\x0102', 'sig1', 'qr1');

    INSERT INTO fiscal.receipt (coupon_id, verification_no, coupon_type, reference_coupon_id, business_nui, branch_id, pos_id,
        terminal_id, application_id, operator_id, issued_at, total_cents, total_tax_cents, pos_coupon, signature, qr_string)
    VALUES (c2, 'ZZZZZZZZ23456789', 3, c1, 812345678, 5130484, 11, 1, 1234, 'anna', now(), 8000, 593, '\x0304', 'sig2', 'qr2');

    -- return without reference must fail
    BEGIN
        INSERT INTO fiscal.receipt (coupon_id, verification_no, coupon_type, business_nui, branch_id, pos_id, terminal_id,
            application_id, operator_id, issued_at, total_cents, total_tax_cents, pos_coupon, signature, qr_string)
        VALUES (fiscal.next_coupon_id(5130484), 'YYYYYYYY23456789', 3, 812345678, 5130484, 11, 1, 1234, 'anna', now(), 1, 0, '\x05', 's', 'q');
        RAISE EXCEPTION 'return without reference was accepted';
    EXCEPTION WHEN check_violation THEN RAISE NOTICE 'PASS return requires reference';
    END;

    -- updates / deletes are blocked
    BEGIN
        UPDATE fiscal.receipt SET total_cents = 1 WHERE coupon_id = c1;
        RAISE EXCEPTION 'update was allowed';
    EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'PASS update blocked';
    END;
    BEGIN
        DELETE FROM fiscal.receipt WHERE coupon_id = c2;
        RAISE EXCEPTION 'delete was allowed';
    EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'PASS delete blocked';
    END;

    -- transmissions: one acceptance only
    INSERT INTO fiscal.transmission (receipt_id, sent_by, outcome, message) VALUES (1, 'client:FRONTDESK-01', 'transient', 'timeout');
    INSERT INTO fiscal.transmission (receipt_id, sent_by, outcome, http_status, atk_transaction_id) VALUES (1, 'server', 'accepted', 200, 18446744073709551615);
    BEGIN
        INSERT INTO fiscal.transmission (receipt_id, sent_by, outcome) VALUES (1, 'server', 'accepted');
        RAISE EXCEPTION 'second acceptance allowed';
    EXCEPTION WHEN unique_violation THEN RAISE NOTICE 'PASS single acceptance';
    END;

    SELECT status = 'accepted' INTO ok FROM fiscal.receipt_status WHERE coupon_id = c1;
    ASSERT ok, 'receipt 1 accepted';
    SELECT count(*) = 1 INTO ok FROM fiscal.offline_queue WHERE coupon_id = c2;
    ASSERT ok, 'receipt 2 pending in offline queue';
    RAISE NOTICE 'PASS status views';

    INSERT INTO fiscal.audit_log (actor, action, entity, entity_id, details) VALUES ('anna', 'LOGIN', NULL, NULL, '{}');
    INSERT INTO fiscal.audit_log (actor, action, entity, entity_id, details) VALUES ('anna', 'EXPORT_CSV', 'offline_queue', NULL, '{"rows":1}');

    ASSERT NOT EXISTS (SELECT 1 FROM fiscal.verify_chain('receipt')), 'receipt chain intact';
    ASSERT NOT EXISTS (SELECT 1 FROM fiscal.verify_chain('audit_log')), 'audit chain intact';
    RAISE NOTICE 'PASS hash chains intact';
END $$;

-- Simulate a superuser tampering with triggers disabled; the chain must expose it.
ALTER TABLE fiscal.receipt DISABLE TRIGGER receipt_no_update_delete;
UPDATE fiscal.receipt SET total_cents = 1 WHERE id = 1;
ALTER TABLE fiscal.receipt ENABLE TRIGGER receipt_no_update_delete;

DO $$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM fiscal.verify_chain('receipt');
    ASSERT r.broken_id = 1, 'tampering detected on row 1';
    RAISE NOTICE 'PASS tampering detected: id=% (%)', r.broken_id, r.reason;
END $$;
