-- A workstation that never issued a receipt can be deleted (e.g. added by mistake or with a wrong POS ID).
-- One with receipts stays: receipt.terminal_id still references it, so the database itself refuses the delete;
-- such a workstation is disabled instead. The audit log keeps the workstation's number in terminal_id without a
-- foreign key (the log is append-only and must not block the delete); the deletion is logged with its details.
DROP TRIGGER IF EXISTS terminal_no_delete ON fiscal.terminal;
ALTER TABLE fiscal.audit_log DROP CONSTRAINT IF EXISTS audit_log_terminal_id_fkey;
GRANT DELETE ON fiscal.terminal TO hrs_fiscal_app;
