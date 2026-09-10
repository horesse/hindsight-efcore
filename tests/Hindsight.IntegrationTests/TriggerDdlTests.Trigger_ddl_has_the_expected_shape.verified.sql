CREATE OR REPLACE FUNCTION policies_history_write() RETURNS trigger
LANGUAGE plpgsql AS $hindsight$
DECLARE
    _now timestamp with time zone := now();
    _closed_at timestamp with time zone;
    _changed_by text := nullif(current_setting('hindsight.changed_by', true), '');
    _changed_by_name text := nullif(current_setting('hindsight.changed_by_name', true), '');
    _correlation_id text := nullif(current_setting('hindsight.correlation_id', true), '');
    _reason text := nullif(current_setting('hindsight.reason', true), '');
    _extra jsonb := nullif(current_setting('hindsight.extra', true), '')::jsonb;
BEGIN
    IF (TG_OP = 'INSERT') THEN
        INSERT INTO policies_history (id, number, premium, status, valid_from, valid_to, operation, changed_by, changed_by_name, correlation_id, reason, extra)
        VALUES (NEW.id, NEW.number, NEW.premium, NEW.status, _now, 'infinity'::timestamp with time zone, 1, _changed_by, _changed_by_name, _correlation_id, _reason, _extra);
        RETURN NEW;
    ELSIF (TG_OP = 'UPDATE') THEN
        IF (NEW.id, NEW.number, NEW.premium, NEW.status) IS NOT DISTINCT FROM (OLD.id, OLD.number, OLD.premium, OLD.status) THEN
            RETURN NEW;
        END IF;
        UPDATE policies_history SET valid_to = GREATEST(_now, valid_from + INTERVAL '1 microsecond') WHERE id = OLD.id AND valid_to = 'infinity'::timestamp with time zone RETURNING valid_to INTO _closed_at;
        INSERT INTO policies_history (id, number, premium, status, valid_from, valid_to, operation, changed_by, changed_by_name, correlation_id, reason, extra)
        VALUES (NEW.id, NEW.number, NEW.premium, NEW.status, COALESCE(_closed_at, _now), 'infinity'::timestamp with time zone, 2, _changed_by, _changed_by_name, _correlation_id, _reason, _extra);
        RETURN NEW;
    ELSIF (TG_OP = 'DELETE') THEN
        UPDATE policies_history SET valid_to = GREATEST(_now, valid_from + INTERVAL '1 microsecond') WHERE id = OLD.id AND valid_to = 'infinity'::timestamp with time zone RETURNING valid_to INTO _closed_at;
        INSERT INTO policies_history (id, number, premium, status, valid_from, valid_to, operation, changed_by, changed_by_name, correlation_id, reason, extra)
        VALUES (OLD.id, OLD.number, OLD.premium, OLD.status, COALESCE(_closed_at, _now), COALESCE(_closed_at, _now), 3, _changed_by, _changed_by_name, _correlation_id, _reason, _extra);
        RETURN OLD;
    END IF;
    RETURN NULL;
END;
$hindsight$;


CREATE OR REPLACE TRIGGER policies_history_trg AFTER INSERT OR UPDATE OR DELETE ON policies FOR EACH ROW EXECUTE FUNCTION policies_history_write();
