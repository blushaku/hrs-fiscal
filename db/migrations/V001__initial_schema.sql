-- HRS Fiscal Solution — initial schema (PostgreSQL 14+)
--
-- Legal drivers (Kosovo):
--   * UA MF 01/2026 Art 46: keep all issued fiscal coupons (retention per Law on Tax Administration Procedures)
--   * ATK technical requirements Art 26: write-once storage, no deletion, corrections only via new linked
--     coupons, audit log of all events, exportable to CSV/PDF; amendment 22.06.2026: data integrity guaranteed
--
-- Design:
--   * receipt, transmission, audit_log and source_payload are APPEND-ONLY (UPDATE/DELETE/TRUNCATE blocked by triggers)
--   * receipt and audit_log rows are hash-chained (row_hash = sha256(prev_hash || canonical row)), so any
--     tampering done by bypassing triggers (e.g. as superuser) is detectable with fiscal.verify_chain()
--   * mutable state (terminal registry, settings) lives in separate tables whose changes are audited

CREATE SCHEMA IF NOT EXISTS fiscal;
SET search_path = fiscal;

-- ---------------------------------------------------------------------------
-- Master data
-- ---------------------------------------------------------------------------

CREATE TABLE business (
    nui                 bigint PRIMARY KEY CHECK (nui > 0),          -- ATK BusinessId
    name                text   NOT NULL,
    fiscalization_no    text   NOT NULL,                             -- from EDI, used for onboarding
    vat_no              text,
    created_at          timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE branch (
    business_nui        bigint NOT NULL REFERENCES business(nui),
    branch_id           bigint NOT NULL CHECK (branch_id > 0 AND branch_id < 900000000), -- unit number registered with ATK; keeps coupon_id within bigint
    name                text   NOT NULL,
    location            text   NOT NULL,                             -- printed on coupon / PosCoupon.Location
    address             text,
    opera_hotel_code    text   UNIQUE,                               -- OPERA Cloud property code
    PRIMARY KEY (business_nui, branch_id)
);

-- One row per workstation running the HRS Fiscal Client (= one ATK "POS").
CREATE TABLE terminal (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    business_nui        bigint NOT NULL,
    branch_id           bigint NOT NULL,
    pos_id              bigint NOT NULL CHECK (pos_id > 0),          -- unique within branch
    opera_terminal_id   text,                                        -- OPERA Fiscal Terminal "Terminal ID"
    hostname            text   NOT NULL,
    client_endpoint     text,                                        -- https://host:port of the HRS Fiscal Client
    certificate_pem     text,                                        -- issued by ATK CA (/ca/signcsr)
    certificate_expires timestamptz,
    status              text   NOT NULL DEFAULT 'pending'
                        CHECK (status IN ('pending', 'active', 'disabled')),
    registered_at       timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (business_nui, branch_id) REFERENCES branch(business_nui, branch_id),
    UNIQUE (business_nui, branch_id, pos_id),
    UNIQUE (branch_id, opera_terminal_id)
);

CREATE TABLE setting (
    key                 text PRIMARY KEY,
    value               jsonb NOT NULL,
    updated_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          text NOT NULL
);

-- CouponId must be unique across the WHOLE business (all branches / all servers).
-- Each server only issues ids for its own branch: coupon_id = branch_id * 10^10 + local sequence.
CREATE SEQUENCE coupon_seq AS bigint MINVALUE 1 MAXVALUE 9999999999 NO CYCLE;

CREATE FUNCTION next_coupon_id(p_branch_id bigint) RETURNS bigint
LANGUAGE sql VOLATILE AS $$
    SELECT p_branch_id * 10000000000 + nextval('fiscal.coupon_seq');
$$;

-- ---------------------------------------------------------------------------
-- Append-only fiscal records
-- ---------------------------------------------------------------------------

-- Raw payload as received from the source system (OPERA OFIS XML/JSON), kept as evidence.
CREATE TABLE source_payload (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source              text   NOT NULL CHECK (source IN ('OFIS', 'POS', 'MANUAL', 'PAPER_BLOCK')),
    source_event_id     text   NOT NULL,                             -- idempotency key from the source
    content_type        text   NOT NULL,
    body                text   NOT NULL,
    received_at         timestamptz NOT NULL DEFAULT now(),
    UNIQUE (source, source_event_id)
);

CREATE TABLE receipt (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    coupon_id           bigint NOT NULL UNIQUE,
    verification_no     varchar(16) NOT NULL UNIQUE,                 -- NUIKF
    coupon_type         smallint NOT NULL CHECK (coupon_type IN (1, 2, 3)),   -- 1 Sale, 2 Cancel, 3 Return
    reference_coupon_id bigint REFERENCES receipt(coupon_id),
    business_nui        bigint NOT NULL,
    branch_id           bigint NOT NULL,
    pos_id              bigint NOT NULL,
    terminal_id         bigint NOT NULL REFERENCES terminal(id),
    application_id      bigint NOT NULL,
    operator_id         text   NOT NULL,
    source_payload_id   bigint REFERENCES source_payload(id),
    source_document     text,                                        -- e.g. OPERA folio number
    issued_at           timestamptz NOT NULL,                        -- PosCoupon.Time
    issued_offline      boolean NOT NULL DEFAULT false,              -- printed "OFFLINE"
    total_cents         bigint NOT NULL CHECK (total_cents >= 0),
    total_tax_cents     bigint NOT NULL CHECK (total_tax_cents >= 0),
    pos_coupon          bytea  NOT NULL,                             -- protobuf bytes exactly as signed
    signature           text   NOT NULL,                             -- Base64 DER ECDSA over Base64(pos_coupon)
    qr_string           text   NOT NULL,                             -- Base64(CitizenCoupon)|signature
    created_at          timestamptz NOT NULL DEFAULT now(),
    prev_hash           bytea,
    row_hash            bytea,
    CHECK ((coupon_type = 1) = (reference_coupon_id IS NULL)),
    FOREIGN KEY (business_nui, branch_id) REFERENCES branch(business_nui, branch_id)
);
CREATE INDEX receipt_issued_at_idx ON receipt (issued_at);
CREATE INDEX receipt_terminal_idx  ON receipt (terminal_id, issued_at);
CREATE INDEX receipt_source_doc_idx ON receipt (source_document);

-- Every attempt to deliver a receipt to ATK.
CREATE TABLE transmission (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    receipt_id          bigint NOT NULL REFERENCES receipt(id),
    attempted_at        timestamptz NOT NULL DEFAULT now(),
    sent_by             text   NOT NULL,                             -- 'client:<hostname>' or 'server'
    outcome             text   NOT NULL CHECK (outcome IN ('accepted', 'rejected', 'transient')),
    http_status         int,
    atk_transaction_id  numeric(20, 0),                              -- uint64
    message             text
);
CREATE INDEX transmission_receipt_idx ON transmission (receipt_id, attempted_at DESC);
CREATE UNIQUE INDEX transmission_one_acceptance ON transmission (receipt_id) WHERE outcome = 'accepted';

-- Audit trail: logins, receipts, transmissions, config changes, exports, reprints, onboarding...
CREATE TABLE audit_log (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    at                  timestamptz NOT NULL DEFAULT now(),
    actor               text   NOT NULL,                             -- user or component
    terminal_id         bigint REFERENCES terminal(id),
    action              text   NOT NULL,                             -- e.g. RECEIPT_ISSUED, EXPORT_CSV, REPRINT
    entity              text,
    entity_id           text,
    details             jsonb  NOT NULL DEFAULT '{}'::jsonb,
    prev_hash           bytea,
    row_hash            bytea
);
CREATE INDEX audit_log_at_idx ON audit_log (at);
CREATE INDEX audit_log_action_idx ON audit_log (action, at);

-- ---------------------------------------------------------------------------
-- Immutability
-- ---------------------------------------------------------------------------

CREATE FUNCTION forbid_modification() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'fiscal.%: % is not allowed; fiscal records are append-only', TG_TABLE_NAME, TG_OP
        USING ERRCODE = 'insufficient_privilege';
END;
$$;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['receipt', 'transmission', 'audit_log', 'source_payload'] LOOP
        EXECUTE format('CREATE TRIGGER %1$s_no_update_delete BEFORE UPDATE OR DELETE ON fiscal.%1$s
                        FOR EACH ROW EXECUTE FUNCTION fiscal.forbid_modification()', t);
        EXECUTE format('CREATE TRIGGER %1$s_no_truncate BEFORE TRUNCATE ON fiscal.%1$s
                        FOR EACH STATEMENT EXECUTE FUNCTION fiscal.forbid_modification()', t);
    END LOOP;
END $$;

-- ---------------------------------------------------------------------------
-- Hash chain
-- ---------------------------------------------------------------------------

CREATE FUNCTION receipt_canonical(r fiscal.receipt) RETURNS bytea
LANGUAGE sql IMMUTABLE AS $$
    SELECT convert_to(concat_ws('|',
        r.id, r.coupon_id, r.verification_no, r.coupon_type, r.reference_coupon_id, r.business_nui,
        r.branch_id, r.pos_id, r.terminal_id, r.application_id, r.operator_id, r.source_payload_id,
        r.source_document, to_char(r.issued_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US'),
        r.issued_offline, r.total_cents, r.total_tax_cents, encode(r.pos_coupon, 'base64'),
        r.signature, r.qr_string), 'UTF8')
$$;

CREATE FUNCTION audit_canonical(a fiscal.audit_log) RETURNS bytea
LANGUAGE sql IMMUTABLE AS $$
    SELECT convert_to(concat_ws('|',
        a.id, to_char(a.at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US'), a.actor, a.terminal_id,
        a.action, a.entity, a.entity_id, a.details::text), 'UTF8')
$$;

CREATE FUNCTION chain_receipt() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('fiscal.receipt.chain'));   -- serialize chain appends
    SELECT row_hash INTO NEW.prev_hash FROM fiscal.receipt ORDER BY id DESC LIMIT 1;
    NEW.prev_hash := coalesce(NEW.prev_hash, '\x00'::bytea);
    NEW.row_hash  := sha256(NEW.prev_hash || fiscal.receipt_canonical(NEW));
    RETURN NEW;
END;
$$;

CREATE FUNCTION chain_audit() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('fiscal.audit_log.chain'));
    SELECT row_hash INTO NEW.prev_hash FROM fiscal.audit_log ORDER BY id DESC LIMIT 1;
    NEW.prev_hash := coalesce(NEW.prev_hash, '\x00'::bytea);
    NEW.row_hash  := sha256(NEW.prev_hash || fiscal.audit_canonical(NEW));
    RETURN NEW;
END;
$$;

CREATE TRIGGER receipt_chain   BEFORE INSERT ON receipt   FOR EACH ROW EXECUTE FUNCTION chain_receipt();
CREATE TRIGGER audit_log_chain BEFORE INSERT ON audit_log FOR EACH ROW EXECUTE FUNCTION chain_audit();

-- Returns the first broken link (if any) in a chain. Empty result = chain intact.
CREATE FUNCTION verify_chain(p_table text DEFAULT 'receipt')
RETURNS TABLE (broken_id bigint, reason text)
LANGUAGE plpgsql STABLE AS $$
DECLARE
    expected_prev bytea := '\x00'::bytea;
    rec record;
BEGIN
    IF p_table = 'receipt' THEN
        FOR rec IN SELECT r.id, r.prev_hash, r.row_hash, fiscal.receipt_canonical(r) AS canon
                   FROM fiscal.receipt r ORDER BY r.id LOOP
            IF rec.prev_hash IS DISTINCT FROM expected_prev THEN
                broken_id := rec.id; reason := 'prev_hash mismatch (row inserted/deleted)'; RETURN NEXT; RETURN;
            END IF;
            IF rec.row_hash IS DISTINCT FROM sha256(rec.prev_hash || rec.canon) THEN
                broken_id := rec.id; reason := 'row_hash mismatch (row modified)'; RETURN NEXT; RETURN;
            END IF;
            expected_prev := rec.row_hash;
        END LOOP;
    ELSIF p_table = 'audit_log' THEN
        FOR rec IN SELECT a.id, a.prev_hash, a.row_hash, fiscal.audit_canonical(a) AS canon
                   FROM fiscal.audit_log a ORDER BY a.id LOOP
            IF rec.prev_hash IS DISTINCT FROM expected_prev THEN
                broken_id := rec.id; reason := 'prev_hash mismatch (row inserted/deleted)'; RETURN NEXT; RETURN;
            END IF;
            IF rec.row_hash IS DISTINCT FROM sha256(rec.prev_hash || rec.canon) THEN
                broken_id := rec.id; reason := 'row_hash mismatch (row modified)'; RETURN NEXT; RETURN;
            END IF;
            expected_prev := rec.row_hash;
        END LOOP;
    ELSE
        RAISE EXCEPTION 'unknown chain %', p_table;
    END IF;
END;
$$;

-- ---------------------------------------------------------------------------
-- Operational views
-- ---------------------------------------------------------------------------

-- Current fiscal status per receipt.
CREATE VIEW receipt_status AS
SELECT r.id, r.coupon_id, r.verification_no, r.terminal_id, r.issued_at, r.issued_offline, r.total_cents,
       a.atk_transaction_id,
       CASE WHEN a.id IS NOT NULL THEN 'accepted'
            WHEN EXISTS (SELECT 1 FROM transmission t WHERE t.receipt_id = r.id AND t.outcome = 'rejected') THEN 'rejected'
            ELSE 'pending' END AS status,
       (SELECT count(*) FROM transmission t WHERE t.receipt_id = r.id) AS attempts,
       (SELECT max(attempted_at) FROM transmission t WHERE t.receipt_id = r.id) AS last_attempt_at
FROM receipt r
LEFT JOIN transmission a ON a.receipt_id = r.id AND a.outcome = 'accepted';

-- Offline queue with legal deadlines: 48h after issue (UA Art 44/45), then 10th of the following month.
CREATE VIEW offline_queue AS
SELECT s.*,
       s.issued_at + interval '48 hours' AS deadline_48h,
       (date_trunc('month', s.issued_at) + interval '1 month' + interval '9 days') AS deadline_month_10th,
       now() > s.issued_at + interval '48 hours' AS overdue_48h
FROM receipt_status s
WHERE s.status = 'pending';

-- ---------------------------------------------------------------------------
-- Least-privilege application role
-- ---------------------------------------------------------------------------

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hrs_fiscal_app') THEN
        CREATE ROLE hrs_fiscal_app NOLOGIN;
    END IF;
END $$;

GRANT USAGE ON SCHEMA fiscal TO hrs_fiscal_app;
GRANT SELECT ON ALL TABLES IN SCHEMA fiscal TO hrs_fiscal_app;
GRANT INSERT ON receipt, transmission, audit_log, source_payload TO hrs_fiscal_app;
GRANT INSERT, UPDATE ON terminal, setting, business, branch TO hrs_fiscal_app;
GRANT USAGE ON SEQUENCE coupon_seq TO hrs_fiscal_app;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA fiscal TO hrs_fiscal_app;
-- No DELETE/TRUNCATE granted anywhere; triggers additionally block UPDATE/DELETE on fiscal records.
