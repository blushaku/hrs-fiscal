-- FLIP message capture. Every request Oracle FLIP sends to HRS Fiscal (HTTP or raw TCP) is stored exactly as
-- received, together with what HRS answered. In "capture" mode this is how the real OFIS/FLIP payload format is
-- learned on an OPERA Cloud demo before Oracle's specification arrives; in live mode it is the evidence trail.
SET search_path = fiscal;

CREATE TABLE flip_message (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    received_at         timestamptz NOT NULL DEFAULT now(),
    transport           text NOT NULL CHECK (transport IN ('http', 'tcp')),
    remote_address      text,
    method              text,                    -- HTTP only
    path                text,                    -- HTTP only (path + query)
    headers             jsonb NOT NULL DEFAULT '{}'::jsonb,
    content_type        text,
    body                bytea NOT NULL,
    mode                text NOT NULL CHECK (mode IN ('capture', 'live')),
    response_status     int,
    response_body       text,
    receipt_id          bigint REFERENCES receipt(id)   -- set in live mode when the message produced a receipt
);
CREATE INDEX flip_message_received_idx ON flip_message (received_at DESC);

CREATE TRIGGER flip_message_no_update_delete BEFORE UPDATE OR DELETE ON flip_message
    FOR EACH ROW EXECUTE FUNCTION forbid_modification();
CREATE TRIGGER flip_message_no_truncate BEFORE TRUNCATE ON flip_message
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_modification();

INSERT INTO setting (key, value, updated_by) VALUES
    ('flip_mode',                  '"capture"'::jsonb, 'migration'),   -- capture | live
    ('flip_stub_status',           '200'::jsonb,       'migration'),
    ('flip_stub_content_type',     '"text/plain"'::jsonb, 'migration'),
    ('flip_stub_body',             '""'::jsonb,        'migration')
ON CONFLICT (key) DO NOTHING;

GRANT SELECT, INSERT ON flip_message TO hrs_fiscal_app;
