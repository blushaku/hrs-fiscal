-- The taxpayer (business + unit) can be changed. Earlier ones stay in the archive with their receipts; exactly one
-- branch is the active one that fiscalizes.
ALTER TABLE fiscal.branch ADD COLUMN active boolean NOT NULL DEFAULT true;
CREATE UNIQUE INDEX branch_one_active ON fiscal.branch ((true)) WHERE active;
