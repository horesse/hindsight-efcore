-- Converts policies_history into a table range-partitioned by valid_to, keeping every row.
-- Run it in one transaction, from a hand-written migration (migrationBuilder.Sql) or psql.
-- The old table is kept, renamed; drop it yourself once you have checked the copy.
BEGIN;

-- No writes to history while it is copied.
LOCK TABLE policies_history IN ACCESS EXCLUSIVE MODE;

-- Move the old table and its indexes out of the way: the new ones take their names.
ALTER TABLE policies_history RENAME TO policies_history_unpartitioned;
ALTER TABLE policies_history_unpartitioned RENAME CONSTRAINT "PK_policies_history" TO "PK_policies_history_unpartitioned";
ALTER INDEX ix_policies_history_version RENAME TO ix_policies_history_unpartitioned_version;
ALTER INDEX ix_policies_history_period RENAME TO ix_policies_history_unpartitioned_period;

-- Same columns, defaults and identity; partitioned by when each version ended.
CREATE TABLE policies_history (LIKE policies_history_unpartitioned INCLUDING DEFAULTS INCLUDING IDENTITY)
    PARTITION BY RANGE (valid_to);

-- Current versions (valid_to = 'infinity') live in their own partition.
CREATE TABLE policies_history_current PARTITION OF policies_history
    FOR VALUES FROM ('infinity') TO (MAXVALUE);

-- One partition per month of closed versions: the last twelve months and the next three.
DO $$
DECLARE
    _month timestamptz;
BEGIN
    FOR _month IN
        SELECT generate_series(
            date_trunc('month', now() AT TIME ZONE 'UTC') - interval '12 months',
            date_trunc('month', now() AT TIME ZONE 'UTC') + interval '3 months',
            interval '1 month') AT TIME ZONE 'UTC'
    LOOP
        EXECUTE format(
            'CREATE TABLE %I PARTITION OF policies_history FOR VALUES FROM (%L) TO (%L)',
            'policies_history_' || to_char(_month AT TIME ZONE 'UTC', 'YYYY_MM'),
            _month,
            _month + interval '1 month');
    END LOOP;
END
$$;

-- Anything no partition covers (older history, or a month nobody created yet) lands here.
CREATE TABLE policies_history_default PARTITION OF policies_history DEFAULT;

-- The key must include the partition key. The two indexes Hindsight creates on every history table.
ALTER TABLE policies_history ADD CONSTRAINT "PK_policies_history" PRIMARY KEY (history_id, valid_to);
CREATE INDEX ix_policies_history_version ON policies_history (id, valid_from DESC);
CREATE INDEX ix_policies_history_period ON policies_history USING gist (tstzrange(valid_from, valid_to));

-- Copy the rows, keeping their history_id, and continue the identity after the highest one.
INSERT INTO policies_history OVERRIDING SYSTEM VALUE SELECT * FROM policies_history_unpartitioned;
SELECT setval(
    pg_get_serial_sequence('policies_history', 'history_id'),
    (SELECT coalesce(max(history_id), 0) + 1 FROM policies_history),
    false);

COMMIT;
