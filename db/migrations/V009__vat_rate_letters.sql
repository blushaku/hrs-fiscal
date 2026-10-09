-- VAT rates can be added from Settings (e.g. when the law introduces a new rate). Letters stay single capitals;
-- ATK must recognise any new letter before it is used on receipts.
ALTER TABLE fiscal.vat_rate DROP CONSTRAINT IF EXISTS vat_rate_letter_check;
ALTER TABLE fiscal.vat_rate ADD CONSTRAINT vat_rate_letter_check CHECK (letter ~ '^[A-Z]$');
