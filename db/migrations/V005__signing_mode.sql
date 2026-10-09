-- Where receipts are signed: selectable per property, with an optional per-workstation override.
--   client: the HRS Fiscal Client on each workstation holds that workstation's ATK key and signs (default;
--           matches ATK's statement that every invoicing workstation needs the software installed).
--   server: the HRS Fiscal Server holds one non-exportable key and ATK certificate PER WORKSTATION and signs on its
--           behalf. ATK still sees each workstation as its own POS. Pending ATK's confirmation that this is allowed.
--
-- A workstation's certificate is bound to the key in the place where it was registered (enrolled_mode). If the
-- effective mode later differs, the workstation must be registered again before it can sign.

ALTER TABLE fiscal.terminal
    ADD COLUMN signing_mode  text CHECK (signing_mode IN ('server', 'client')),   -- NULL = property default
    ADD COLUMN enrolled_mode text CHECK (enrolled_mode IN ('server', 'client')),  -- where the current key/certificate lives
    ADD COLUMN key_reference text;                                                -- server mode: key handle (e.g. cng:<name>), never the key

INSERT INTO fiscal.setting (key, value, updated_by) VALUES ('signing_mode_default', '"client"'::jsonb, 'migration')
ON CONFLICT (key) DO NOTHING;
