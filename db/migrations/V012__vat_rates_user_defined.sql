-- VAT rates are defined by the user: the list starts empty and rates are added under Settings › OPERA overrides & VAT.
-- The rates seeded by V002 are removed, except any that an OPERA override still points to (those stay until the override
-- is changed; they can then be deleted in Settings).
-- Receipts already issued keep their own letter and percentage in the archive; deleting a rate does not change them.
--
-- match_opera: OPERA sends a VAT percentage per line; the rate with match_opera = true and that percentage is used.
-- At most one rate per percentage can match (e.g. "exempt" and "0%" share 0%: only one of them is used automatically,
-- the other only through an override).
ALTER TABLE fiscal.vat_rate ADD COLUMN match_opera boolean NOT NULL DEFAULT true;

DELETE FROM fiscal.vat_rate v
WHERE NOT EXISTS (SELECT 1 FROM fiscal.opera_trx_mapping m WHERE m.vat_letter = v.letter);

-- Kept A and C would both match 0%: A (exempt) then only applies through overrides, as before.
UPDATE fiscal.vat_rate SET match_opera = false
WHERE letter = 'A' AND EXISTS (SELECT 1 FROM fiscal.vat_rate c WHERE c.letter <> 'A' AND c.percent = fiscal.vat_rate.percent);
-- Any other duplicate percentage: only the first letter matches.
UPDATE fiscal.vat_rate v SET match_opera = false
WHERE match_opera AND EXISTS (SELECT 1 FROM fiscal.vat_rate o WHERE o.match_opera AND o.percent = v.percent AND o.letter < v.letter);

CREATE UNIQUE INDEX vat_rate_one_match_per_percent ON fiscal.vat_rate (percent) WHERE match_opera;

GRANT DELETE ON fiscal.vat_rate TO hrs_fiscal_app;
