-- ATK compliance additions to the receipt archive (Doc. 01-06-1967 Art 25.11, 25.16, 25.18, 26.13; UA Arts 7, 45):
--   issued_day / daily_no  daily receipt number per workstation (POS) and property day ("Kupon fiskal ditor nr.")
--   cancel_reason          reason printed on a Cancel receipt ("Arsyeja e anulimit"), required for type 2
--   paper_serial           serial number of a receipt written from the special paper block, entered afterwards
-- Existing rows keep NULL in the new columns; receipt_canonical uses concat_ws, which skips NULLs, so the hash of
-- every existing row is unchanged and the chain still verifies.

ALTER TABLE fiscal.receipt
    ADD COLUMN issued_day    date,
    ADD COLUMN daily_no      integer CHECK (daily_no > 0),
    ADD COLUMN cancel_reason text,
    ADD COLUMN paper_serial  text;

ALTER TABLE fiscal.receipt ADD CONSTRAINT receipt_cancel_has_reason
    CHECK (coupon_type <> 2 OR length(btrim(coalesce(cancel_reason, ''))) > 0) NOT VALID;

CREATE UNIQUE INDEX receipt_daily_no_unique ON fiscal.receipt (terminal_id, issued_day, daily_no) WHERE daily_no IS NOT NULL;
CREATE UNIQUE INDEX receipt_paper_serial_unique ON fiscal.receipt (business_nui, paper_serial) WHERE paper_serial IS NOT NULL;
CREATE UNIQUE INDEX receipt_one_cancel ON fiscal.receipt (reference_coupon_id) WHERE coupon_type = 2;

CREATE OR REPLACE FUNCTION fiscal.receipt_canonical(r fiscal.receipt) RETURNS bytea
LANGUAGE sql IMMUTABLE AS $$
    SELECT convert_to(concat_ws('|',
        r.id, r.coupon_id, r.verification_no, r.coupon_type, r.reference_coupon_id, r.business_nui,
        r.branch_id, r.pos_id, r.terminal_id, r.application_id, r.operator_id, r.source_payload_id,
        r.source_document, to_char(r.issued_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US'),
        r.issued_offline, r.total_cents, r.total_tax_cents, encode(r.pos_coupon, 'base64'),
        r.signature, r.qr_string,
        to_char(r.issued_day, 'YYYY-MM-DD'), r.daily_no, r.cancel_reason, r.paper_serial), 'UTF8')
$$;

-- The chain lock already serializes receipt inserts, so the daily number is assigned here without races.
CREATE OR REPLACE FUNCTION fiscal.chain_receipt() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('fiscal.receipt.chain'));   -- serialize chain appends
    IF NEW.issued_day IS NOT NULL AND NEW.daily_no IS NULL THEN
        SELECT coalesce(max(daily_no), 0) + 1 INTO NEW.daily_no
        FROM fiscal.receipt WHERE terminal_id = NEW.terminal_id AND issued_day = NEW.issued_day;
    END IF;
    SELECT row_hash INTO NEW.prev_hash FROM fiscal.receipt ORDER BY id DESC LIMIT 1;
    NEW.prev_hash := coalesce(NEW.prev_hash, '\x00'::bytea);
    NEW.row_hash  := sha256(NEW.prev_hash || fiscal.receipt_canonical(NEW));
    RETURN NEW;
END;
$$;

-- offline: issued while ATK could not be reached, i.e. the first attempt at issue time did not get through
-- (or flagged at issue). Such receipts carry the text "OFFLINE" (Art 26.13, 28.6). Paper-block entries are not
-- offline receipts: they were printed on paper and carry their paper serial.
CREATE OR REPLACE VIEW fiscal.receipt_status AS
SELECT r.id, r.coupon_id, r.verification_no, r.terminal_id, r.issued_at, r.issued_offline, r.total_cents,
       a.atk_transaction_id,
       CASE WHEN a.id IS NOT NULL THEN 'accepted'
            WHEN EXISTS (SELECT 1 FROM fiscal.transmission t WHERE t.receipt_id = r.id AND t.outcome = 'rejected') THEN 'rejected'
            ELSE 'pending' END AS status,
       (SELECT count(*) FROM fiscal.transmission t WHERE t.receipt_id = r.id) AS attempts,
       (SELECT max(attempted_at) FROM fiscal.transmission t WHERE t.receipt_id = r.id) AS last_attempt_at,
       r.paper_serial IS NULL AND (r.issued_offline OR coalesce(
           (SELECT t.outcome = 'transient' FROM fiscal.transmission t WHERE t.receipt_id = r.id ORDER BY t.id LIMIT 1), false)) AS offline
FROM fiscal.receipt r
LEFT JOIN fiscal.transmission a ON a.receipt_id = r.id AND a.outcome = 'accepted';

-- A workstation's central signing is now the default: the Fiscal Client is not available yet, and a new
-- installation must be able to fiscalize as delivered. Only changes the value if it was never changed by a user.
UPDATE fiscal.setting SET value = '"server"'::jsonb, updated_by = 'migration'
WHERE key = 'signing_mode_default' AND value = '"client"'::jsonb AND updated_by = 'migration';

-- Clock check against ATK (Art 26.5): last measured difference, in seconds (ATK time minus server time).
INSERT INTO fiscal.setting (key, value, updated_by) VALUES ('clock_max_drift_seconds', '30'::jsonb, 'migration')
ON CONFLICT (key) DO NOTHING;

-- New permissions: cancellation receipts and paper-block entry (Admin always has every permission).
INSERT INTO fiscal.role_permission (role, permission) VALUES
    ('Supervisor', 'receipts.cancel'), ('Supervisor', 'receipts.paper')
ON CONFLICT DO NOTHING;
