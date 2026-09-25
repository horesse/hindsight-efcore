CREATE TABLE hindsight_retention_horizon (
    history_entity text NOT NULL,
    horizon timestamp with time zone NOT NULL,
    CONSTRAINT "PK_hindsight_retention_horizon" PRIMARY KEY (history_entity)
);


CREATE OR REPLACE FUNCTION hindsight_history_retained(_history_entity text, _at timestamp with time zone) RETURNS boolean
LANGUAGE plpgsql STABLE PARALLEL SAFE AS $hindsight$
DECLARE
    _horizon timestamp with time zone;
BEGIN
    SELECT r.horizon INTO _horizon FROM hindsight_retention_horizon AS r WHERE r.history_entity = _history_entity;
    IF _horizon IS NOT NULL AND _at < _horizon THEN
        RAISE EXCEPTION USING
            ERRCODE = 'HS001',
            MESSAGE = format('The history of %s was pruned before %s (its retention horizon), so a historical query for %s would answer from incomplete history.', _history_entity, _horizon, _at),
            HINT = 'Query an instant at or after the retention horizon.';
    END IF;
    RETURN true;
END;
$hindsight$;
