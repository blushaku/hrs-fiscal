-- Defaults for receipt lines when OPERA does not say (and no override is set for the transaction code).
INSERT INTO fiscal.setting (key, value, updated_by) VALUES
    ('default_item_category', '"TT"'::jsonb,   'migration'),   -- ATK category "Të tjera" (other); override per code, e.g. HT for rooms
    ('default_item_unit',     '"cope"'::jsonb, 'migration')
ON CONFLICT (key) DO NOTHING;
