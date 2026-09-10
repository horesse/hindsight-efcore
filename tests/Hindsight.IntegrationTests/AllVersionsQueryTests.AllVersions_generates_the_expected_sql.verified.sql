SELECT p.id AS "Id", p.metadata AS "Metadata", p.number AS "Number", p.premium AS "Premium", p.status AS "Status", p.tags AS "Tags"
FROM policies_history AS p
WHERE p.operation <> 3 AND p.status = 'Active'
ORDER BY p.valid_from DESC