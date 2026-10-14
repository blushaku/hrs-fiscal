-- Fiscal receipts printed on a thermal printer (Doc. 01-06-1967 Art 26.14: 58–100 mm paper) as an alternative to the
-- fiscal block on the OPERA folio. The choice is the setting receipt_print_mode ("folio" | "printer"); each workstation
-- has its own printer: a network ESC/POS printer (host:port, usually 9100) or a printer installed in Windows on the
-- fiscal server (printer name). Printer settings are configuration, not fiscal data, so they can be changed.
ALTER TABLE fiscal.terminal
    ADD COLUMN printer_kind     text     NOT NULL DEFAULT 'none' CHECK (printer_kind IN ('none', 'network', 'windows')),
    ADD COLUMN printer_address  text,
    ADD COLUMN printer_paper_mm smallint NOT NULL DEFAULT 80 CHECK (printer_paper_mm IN (58, 80)),
    ADD COLUMN printer_auto     boolean  NOT NULL DEFAULT true;

INSERT INTO fiscal.setting (key, value, updated_by) VALUES ('receipt_print_mode', '"folio"', 'migration')
ON CONFLICT (key) DO NOTHING;
