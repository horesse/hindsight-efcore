SELECT f.id, f."IsActive", f.tenant_id
FROM filtered_policies_history AS f
WHERE f.tenant_id = @ef_filter__CurrentTenantId AND f.tenant_id = @ef_filter__TenantId AND f.tenant_id = @ef_filter__p AND f."IsActive" AND f.valid_from <= @AsOfUtc AND f.valid_to > @AsOfUtc