-- HRS Fiscal Solution — admin users, configuration tables, default settings.
-- Every change to these tables is written to fiscal.audit_log by the application (old and new values).
SET search_path = fiscal;

CREATE TABLE app_user (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    username            text NOT NULL UNIQUE CHECK (username = lower(username) AND length(username) BETWEEN 3 AND 64),
    display_name        text NOT NULL,
    role                text NOT NULL CHECK (role IN ('Cashier', 'Supervisor', 'Admin', 'Auditor')),
    password_hash       text NOT NULL,                     -- ASP.NET Core Identity PBKDF2 format
    active              boolean NOT NULL DEFAULT true,
    created_at          timestamptz NOT NULL DEFAULT now()
);

-- VAT rate per ATK letter. Letters are fixed by ATK; percentages can change by law.
CREATE TABLE vat_rate (
    letter              char(1) PRIMARY KEY CHECK (letter IN ('A', 'C', 'D', 'E')),
    percent             numeric(5, 2) NOT NULL CHECK (percent >= 0 AND percent < 100),
    description         text NOT NULL
);
INSERT INTO vat_rate (letter, percent, description) VALUES
    ('A', 0, 'Exempt from VAT'),
    ('C', 0, 'VAT 0%'),
    ('D', 8, 'Reduced VAT rate'),
    ('E', 18, 'Standard VAT rate');

-- Optional per-code overrides. By default every field comes from OPERA via FLIP; a row here overrides it.
CREATE TABLE opera_trx_mapping (
    branch_id           bigint NOT NULL,
    trx_code            text   NOT NULL,
    item_name           text   NOT NULL,
    unit                text   NOT NULL DEFAULT 'cope',
    category            text   NOT NULL,                   -- ATK goods/service category, e.g. HT, UR, SUA
    vat_letter          char(1) NOT NULL REFERENCES vat_rate(letter),
    active              boolean NOT NULL DEFAULT true,
    updated_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          text NOT NULL,
    PRIMARY KEY (branch_id, trx_code)
);

-- Optional override: OPERA payment method -> ATK PaymentType (1 Cash, 2 CreditCard, 3 Voucher, 4 Cheque, 5 CryptoCurrency, 6 Other).
CREATE TABLE opera_payment_mapping (
    branch_id           bigint NOT NULL,
    payment_code        text   NOT NULL,
    description         text   NOT NULL,
    atk_payment_type    smallint NOT NULL CHECK (atk_payment_type BETWEEN 1 AND 6),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          text NOT NULL,
    PRIMARY KEY (branch_id, payment_code)
);

ALTER TABLE terminal ADD COLUMN description text;

-- Users and terminals are never deleted (audit entries and receipts refer to them); deactivate instead.
CREATE TRIGGER app_user_no_delete BEFORE DELETE ON app_user FOR EACH ROW EXECUTE FUNCTION forbid_modification();
CREATE TRIGGER terminal_no_delete BEFORE DELETE ON terminal FOR EACH ROW EXECUTE FUNCTION forbid_modification();

INSERT INTO setting (key, value, updated_by) VALUES
    ('retention_years',      '10'::jsonb,             'migration'),   -- placeholder until the legal period is confirmed
    ('backup_target',        '""'::jsonb,             'migration'),
    ('atk_environment',      '"Test"'::jsonb,         'migration'),
    ('atk_application_id',   '0'::jsonb,              'migration'),
    ('atk_timeout_seconds',  '10'::jsonb,             'migration'),
    ('atk_retry_minutes',    '2'::jsonb,              'migration'),
    ('alert_emails',         '""'::jsonb,             'migration'),
    ('vat_rounding',         '"RoundTaxHalfUp"'::jsonb, 'migration')  -- pending ATK confirmation
ON CONFLICT (key) DO NOTHING;

GRANT SELECT, INSERT, UPDATE ON app_user, vat_rate, opera_trx_mapping, opera_payment_mapping TO hrs_fiscal_app;
GRANT DELETE ON opera_trx_mapping, opera_payment_mapping TO hrs_fiscal_app;
