SELECT p.id, p.metadata, p.number, p.premium, p.status, p.tags, p.valid_from::timestamptz, p.valid_to::timestamptz, p.operation::smallint, p.changed_by, p.changed_by_name, p.correlation_id, p.reason, p.extra
FROM policies_history AS p
WHERE p.status = 'Active'
ORDER BY p.valid_from DESC, p.history_id DESC