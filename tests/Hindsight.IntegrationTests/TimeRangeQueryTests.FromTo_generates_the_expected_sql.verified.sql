-- @FromUtc='2026-01-01T08:00:00.0000000Z' (DbType = DateTime)
-- @ToUtc='2026-01-01T09:00:00.0000000Z' (DbType = DateTime)
SELECT p.id, p.number, p.premium, p.status
FROM policies_history AS p
WHERE p.operation <> 3 AND tstzrange(p.valid_from, p.valid_to) && tstzrange(@FromUtc, @ToUtc) AND p.status = 'Active'
ORDER BY p.valid_from DESC