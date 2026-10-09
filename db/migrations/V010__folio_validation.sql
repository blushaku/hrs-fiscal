-- Folio validation before fiscalization (Settings › General › Validation).
INSERT INTO fiscal.setting (key, value, updated_by) VALUES
    ('validation_check_tax_number', 'true'::jsonb,  'migration'),  -- DocumentInfo.PropertyTaxNumber must be this business
    ('validation_tolerance',        '0.01'::jsonb,  'migration')   -- EUR per line for rounding differences with OPERA
ON CONFLICT (key) DO NOTHING;
