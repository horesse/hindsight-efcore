-- @AsOfUtc='1970-01-01T00:00:00.0000000Z' (DbType = DateTime)
SELECT p.id, p.number, p.address_city, p.address_street, p.address_geo_lat, p.billing_iban IS NULL, p.billing_day, p.billing_iban, p.holder_name IS NULL, p.holder_name, p.holder_contact_phone IS NULL, p.holder_contact_phone, p.marketing_channel IS NULL, p.marketing_score IS NULL, p.marketing_channel, p.marketing_score
FROM policies_history AS p
WHERE p.valid_from <= @AsOfUtc AND p.valid_to > @AsOfUtc