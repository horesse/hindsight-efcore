-- @AsOfUtc='2026-01-01T09:00:00.0000000Z' (DbType = DateTime)
SELECT p.id AS "Id", p.metadata AS "Metadata", p.number AS "Number", p.premium AS "Premium", p.status AS "Status", p.tags AS "Tags"
FROM policies_history AS p
WHERE p.valid_from <= @AsOfUtc AND p.valid_to > @AsOfUtc AND p.status = 'Active'
ORDER BY p.number DESC