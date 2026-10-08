-- Switch for the optional OPERA overrides (per transaction code / payment method).
-- false (default): receipts are fiscalized exactly as OPERA sends them via FLIP; override tables are ignored.
-- true: entries in opera_trx_mapping / opera_payment_mapping override OPERA's values for those codes.
INSERT INTO fiscal.setting (key, value, updated_by) VALUES ('opera_overrides_enabled', 'false'::jsonb, 'migration')
ON CONFLICT (key) DO NOTHING;
