-- One-time compatibility upgrade for deployments that created external IdPs
-- only as tenant-scoped providers before platform-scoped providers existed.
--
-- What it does:
--   - copies selected tenant IdentityProviders to platform IdentityProviders
--     by setting TenantId to NULL on the copied rows;
--   - preserves provider configuration, display settings, logos, and enabled state;
--   - copies provider claim mappings and provider keys to the copied platform rows;
--   - skips platform providers/mappings/keys that already exist so the script is rerunnable.
--
-- Recommended usage:
--   Take a database backup first, then run this against the MrWhoOidc database.
--
--   psql "$CONNECTION_STRING" \
--     -v ON_ERROR_STOP=1 \
--     -c "SET mrwho.upgrade.source_tenant_slug = 'default';" \
--     -f scripts/upgrade-platform-providers-from-tenant-idps.sql
--
-- If mrwho.upgrade.source_tenant_slug is omitted, the script considers every
-- tenant-scoped provider. In that mode provider names must be unique across all
-- tenants, because platform provider names are unique.
--
-- After running, make sure each upstream IdP app allows the platform callback:
--   https://<platform-host>/auth/external/callback

CREATE OR REPLACE FUNCTION pg_temp.mrwho_deterministic_uuid(input text)
RETURNS uuid
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT (
        substr(md5(input), 1, 8) || '-' ||
        substr(md5(input), 9, 4) || '-' ||
        substr(md5(input), 13, 4) || '-' ||
        substr(md5(input), 17, 4) || '-' ||
        substr(md5(input), 21, 12)
    )::uuid;
$$;

BEGIN;

DO $$
DECLARE
    source_tenant_slug text := NULLIF(current_setting('mrwho.upgrade.source_tenant_slug', true), '');
    duplicate_names text;
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns c
        WHERE c.table_name = 'IdentityProviders'
          AND c.column_name = 'TenantId'
          AND c.is_nullable = 'NO'
    ) THEN
        RAISE EXCEPTION 'IdentityProviders.TenantId is not nullable. Apply the AddPlatformIdentityProviders migration before running this upgrade script.';
    END IF;

    IF source_tenant_slug IS NOT NULL AND NOT EXISTS (
        SELECT 1
        FROM "Tenants" t
        WHERE t."Slug" = source_tenant_slug
    ) THEN
        RAISE EXCEPTION 'No tenant exists with slug %.', source_tenant_slug;
    END IF;

    SELECT string_agg(format('%s (%s)', d."Name", d.tenant_slugs), ', ' ORDER BY d."Name")
    INTO duplicate_names
    FROM (
        SELECT p."Name", string_agg(t."Slug", ', ' ORDER BY t."Slug") AS tenant_slugs
        FROM "IdentityProviders" p
        JOIN "Tenants" t ON t."Id" = p."TenantId"
        WHERE p."TenantId" IS NOT NULL
          AND (source_tenant_slug IS NULL OR t."Slug" = source_tenant_slug)
        GROUP BY p."Name"
        HAVING COUNT(*) > 1
    ) d;

    IF duplicate_names IS NOT NULL THEN
        RAISE EXCEPTION 'Cannot initialize platform providers because selected tenant providers contain duplicate names: %. Re-run with SET mrwho.upgrade.source_tenant_slug = ''<tenant-slug>'' to choose one tenant.', duplicate_names;
    END IF;
END $$;

WITH source_providers AS (
    SELECT
        p.*,
        pg_temp.mrwho_deterministic_uuid('mrwho-platform-provider:' || p."Id"::text) AS "PlatformProviderId"
    FROM "IdentityProviders" p
    JOIN "Tenants" t ON t."Id" = p."TenantId"
    WHERE p."TenantId" IS NOT NULL
      AND (NULLIF(current_setting('mrwho.upgrade.source_tenant_slug', true), '') IS NULL
           OR t."Slug" = NULLIF(current_setting('mrwho.upgrade.source_tenant_slug', true), ''))
), inserted_platform_providers AS (
    INSERT INTO "IdentityProviders" (
        "Id",
        "TenantId",
        "Name",
        "DisplayName",
        "Type",
        "ProviderTemplate",
        "Enabled",
        "IsDefault",
        "AllowRegistration",
        "LogoStorageType",
        "LogoUrl",
        "LogoData",
        "LogoContentType",
        "SortOrder",
        "ConfigJson",
        "ProviderSpecificConfigJson",
        "ButtonBackgroundColor",
        "ButtonTextColor",
        "CreatedAt",
        "UpdatedAt"
    )
    SELECT
        sp."PlatformProviderId",
        NULL,
        sp."Name",
        sp."DisplayName",
        sp."Type",
        sp."ProviderTemplate",
        sp."Enabled",
        sp."IsDefault",
        sp."AllowRegistration",
        sp."LogoStorageType",
        sp."LogoUrl",
        sp."LogoData",
        sp."LogoContentType",
        sp."SortOrder",
        sp."ConfigJson",
        sp."ProviderSpecificConfigJson",
        sp."ButtonBackgroundColor",
        sp."ButtonTextColor",
        now(),
        now()
    FROM source_providers sp
    WHERE NOT EXISTS (
        SELECT 1
        FROM "IdentityProviders" existing
        WHERE existing."TenantId" IS NULL
          AND existing."Name" = sp."Name"
    )
    RETURNING "Id", "Name"
), platform_targets AS (
    SELECT existing."Id", existing."Name"
    FROM "IdentityProviders" existing
    WHERE existing."TenantId" IS NULL

    UNION ALL

    SELECT inserted."Id", inserted."Name"
    FROM inserted_platform_providers inserted
), provider_map AS (
    SELECT
        sp."Id" AS "SourceProviderId",
        platform."Id" AS "PlatformProviderId"
    FROM source_providers sp
    JOIN platform_targets platform ON platform."Name" = sp."Name"
), inserted_claim_mappings AS (
    INSERT INTO "IdentityProviderClaimMappings" (
        "Id",
        "IdentityProviderId",
        "ExternalClaim",
        "LocalClaim",
        "Transform",
        "Order"
    )
    SELECT
        pg_temp.mrwho_deterministic_uuid('mrwho-platform-provider-claim-mapping:' || cm."Id"::text),
        pm."PlatformProviderId",
        cm."ExternalClaim",
        cm."LocalClaim",
        cm."Transform",
        cm."Order"
    FROM "IdentityProviderClaimMappings" cm
    JOIN provider_map pm ON pm."SourceProviderId" = cm."IdentityProviderId"
    WHERE NOT EXISTS (
        SELECT 1
        FROM "IdentityProviderClaimMappings" existing
        WHERE existing."Id" = pg_temp.mrwho_deterministic_uuid('mrwho-platform-provider-claim-mapping:' || cm."Id"::text)
           OR (
               existing."IdentityProviderId" = pm."PlatformProviderId"
               AND existing."ExternalClaim" = cm."ExternalClaim"
               AND existing."LocalClaim" = cm."LocalClaim"
               AND existing."Transform" IS NOT DISTINCT FROM cm."Transform"
               AND existing."Order" = cm."Order"
           )
    )
    RETURNING "Id"
), inserted_provider_keys AS (
    INSERT INTO "IdentityProviderKeys" (
        "Id",
        "IdentityProviderId",
        "Purpose",
        "Jwk",
        "Alg",
        "Active",
        "Publishable",
        "Kid",
        "CreatedAt",
        "ExpiresAt"
    )
    SELECT
        pg_temp.mrwho_deterministic_uuid('mrwho-platform-provider-key:' || k."Id"::text),
        pm."PlatformProviderId",
        k."Purpose",
        k."Jwk",
        k."Alg",
        k."Active",
        k."Publishable",
        k."Kid",
        k."CreatedAt",
        k."ExpiresAt"
    FROM "IdentityProviderKeys" k
    JOIN provider_map pm ON pm."SourceProviderId" = k."IdentityProviderId"
    WHERE NOT EXISTS (
        SELECT 1
        FROM "IdentityProviderKeys" existing
        WHERE existing."Id" = pg_temp.mrwho_deterministic_uuid('mrwho-platform-provider-key:' || k."Id"::text)
           OR (
               existing."IdentityProviderId" = pm."PlatformProviderId"
               AND existing."Purpose" = k."Purpose"
               AND existing."Kid" IS NOT DISTINCT FROM k."Kid"
               AND existing."Jwk" = k."Jwk"
           )
    )
    RETURNING "Id"
)
SELECT
    (SELECT COUNT(*) FROM source_providers) AS source_provider_count,
    (SELECT COUNT(*) FROM inserted_platform_providers) AS platform_providers_inserted,
    (SELECT COUNT(*) FROM inserted_claim_mappings) AS claim_mappings_inserted,
    (SELECT COUNT(*) FROM inserted_provider_keys) AS provider_keys_inserted;

COMMIT;
