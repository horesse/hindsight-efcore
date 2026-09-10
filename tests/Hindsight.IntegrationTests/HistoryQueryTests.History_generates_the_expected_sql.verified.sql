SELECT p.id AS "Id", p.metadata AS "Metadata", p.number AS "Number", p.premium AS "Premium", p.status AS "Status", p.tags AS "Tags", p.valid_from::timestamptz AS "ValidFrom", p.valid_to::timestamptz AS "ValidTo", p.operation::smallint AS "Operation", p.changed_by AS "ChangedBy", p.changed_by_name AS "ChangedByName", p.correlation_id AS "CorrelationId", p.reason AS "Reason", p.extra AS "Extra"
FROM policies_history AS p
WHERE p.status = 'Active'
ORDER BY p.valid_from DESC, p.history_id DESC