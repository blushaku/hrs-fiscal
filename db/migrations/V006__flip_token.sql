-- Access token for FLIP → HRS Fiscal Server. Only a SHA-256 hash is stored; the token is shown once when generated.
-- flip_auth_required stays false until an administrator generates a token (Settings › General), which switches it on.
INSERT INTO fiscal.setting (key, value, updated_by) VALUES
    ('flip_auth_required', 'false'::jsonb,           'migration'),
    ('flip_auth_header',   '"Authorization"'::jsonb, 'migration')   -- header FLIP puts the token in
ON CONFLICT (key) DO NOTHING;

-- Requests refused for a missing/wrong token are recorded (without body) with mode 'rejected'.
ALTER TABLE fiscal.flip_message DROP CONSTRAINT flip_message_mode_check;
ALTER TABLE fiscal.flip_message ADD CONSTRAINT flip_message_mode_check CHECK (mode IN ('capture', 'live', 'rejected'));
