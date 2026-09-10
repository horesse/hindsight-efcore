SELECT p.id, p.metadata, p.number, p.premium, p.status, p.tags
FROM policies_history AS p
WHERE p.operation <> 3 AND p.status = 'Active'
ORDER BY p.valid_from DESC