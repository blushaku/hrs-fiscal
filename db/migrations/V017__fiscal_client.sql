-- Workstation-client signing: the Fiscal Client on the front-desk PC keeps the workstation's key, registers with ATK
-- through the server and signs receipts when the server asks.
--   client_pairing   one-time pairing code of the installation package (protected by the server's data protection key)
--   client_pairing_expires
--   client_secret    shared secret for the HMAC-signed requests between server and client (protected)
--   client_seen_at / client_version / client_printers   from the client's heartbeat (printers and COM ports on the PC)
-- Thermal printers on the PC (USB or COM) are printed by its Fiscal Client: printer kinds client-usb and client-serial.
ALTER TABLE fiscal.terminal
    ADD COLUMN client_pairing          text,
    ADD COLUMN client_pairing_expires  timestamptz,
    ADD COLUMN client_secret           text,
    ADD COLUMN client_paired_at        timestamptz,
    ADD COLUMN client_seen_at          timestamptz,
    ADD COLUMN client_version          text,
    ADD COLUMN client_printers         jsonb;
ALTER TABLE fiscal.terminal DROP CONSTRAINT IF EXISTS terminal_printer_kind_check;
ALTER TABLE fiscal.terminal ADD CONSTRAINT terminal_printer_kind_check
    CHECK (printer_kind IN ('none', 'network', 'usb', 'serial', 'client-usb', 'client-serial'));
