-- Which role may do what. Editable by users with users.manage (Settings › Roles & permissions); every change is audited.
-- Admin always has every permission (enforced in code), so an administrator can never lock everyone out.
CREATE TABLE fiscal.role_permission (
    role        text NOT NULL CHECK (role IN ('Cashier', 'Supervisor', 'Admin', 'Auditor')),
    permission  text NOT NULL,
    PRIMARY KEY (role, permission)
);

INSERT INTO fiscal.role_permission (role, permission) VALUES
    ('Cashier',    'receipts.view'), ('Cashier', 'receipts.copy'),
    ('Supervisor', 'dashboard.system'), ('Supervisor', 'receipts.view'), ('Supervisor', 'receipts.copy'), ('Supervisor', 'export'),
    ('Supervisor', 'audit.view'), ('Supervisor', 'audit.integrity'), ('Supervisor', 'flip.view'),
    ('Auditor',    'receipts.view'), ('Auditor', 'export'), ('Auditor', 'audit.view'), ('Auditor', 'audit.integrity');

GRANT SELECT, INSERT, DELETE ON fiscal.role_permission TO hrs_fiscal_app;
