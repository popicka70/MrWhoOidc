-- IdP Chaining Configuration Diagnostic Script
-- 
-- This script helps diagnose and fix IdP chaining configuration issues where
-- the second IdP in a chain bypasses the provider picker and goes directly to
-- username/password login.
--
-- Usage:
--   1. Run the diagnostic queries to identify misconfigured clients
--   2. Run the fix queries (UPDATE statements) to correct the configuration
--   3. Verify the changes
--
-- Prerequisites:
--   - You know the client_id that represents the upstream IdP in your database
--   - You have access to both IdP databases if they're separate

-- ==============================================================================
-- SECTION 1: Diagnostic Queries
-- ==============================================================================

-- 1.1: Find all clients that might represent external IdPs
--      (look for clients with names suggesting they're used by other IdPs)
SELECT 
    "ClientId",
    "ClientName",
    "AllowLocalLogin",
    "AllowExternalIdp",
    "AllowQrLogin",
    "Id"
FROM "Clients"
WHERE "ClientId" LIKE '%idp%' 
   OR "ClientId" LIKE '%provider%'
   OR "ClientId" LIKE '%upstream%'
ORDER BY "ClientId";

-- 1.2: Check a specific client's configuration
--      Replace 'YOUR_CLIENT_ID' with the actual client_id
SELECT 
    "ClientId",
    "ClientName",
    "AllowLocalLogin",
    "AllowExternalIdp", 
    "AllowQrLogin",
    "RequirePkce",
    "RealmId",
    "Id"
FROM "Clients"
WHERE "ClientId" = 'YOUR_CLIENT_ID';

-- 1.3: Check provider mappings for a specific client
--      Replace 'YOUR_CLIENT_ID' with the actual client_id
SELECT 
    c."ClientId",
    c."ClientName",
    ip."Name" AS "ProviderName",
    ip."DisplayName" AS "ProviderDisplayName",
    cip."Enabled" AS "MappingEnabled",
    cip."AutoRedirectIfSingle",
    cip."Order",
    ip."Enabled" AS "ProviderEnabled"
FROM "ClientIdentityProviders" cip
JOIN "Clients" c ON c."Id" = cip."ClientId"
JOIN "IdentityProviders" ip ON ip."Id" = cip."IdentityProviderId"
WHERE c."ClientId" = 'YOUR_CLIENT_ID'
ORDER BY cip."Order";

-- 1.4: Find external OIDC providers and their configured client IDs
--      This helps you identify which clients in the downstream IdP represent upstream IdPs
SELECT 
    "Name" AS "ProviderName",
    "DisplayName",
    "Enabled",
    "ConfigJson" ->> 'ClientId' AS "UpstreamClientId",
    "ConfigJson" ->> 'Authority' AS "Authority"
FROM "IdentityProviders"
WHERE "Type" = 0 -- OIDC
  AND "Enabled" = true
ORDER BY "Name";

-- 1.5: Find clients with restrictive login settings (likely misconfigured for IdP chaining)
SELECT 
    "ClientId",
    "ClientName",
    "AllowLocalLogin",
    "AllowExternalIdp",
    "AllowQrLogin"
FROM "Clients"
WHERE ("AllowLocalLogin" = false AND "AllowExternalIdp" = false AND "AllowQrLogin" = false)
   OR ("AllowExternalIdp" = false AND "AllowQrLogin" = false)
ORDER BY "ClientId";

-- ==============================================================================
-- SECTION 2: Fix Queries
-- ==============================================================================

-- 2.1: Enable all login methods for a specific client
--      Replace 'YOUR_CLIENT_ID' with the client representing the upstream IdP
UPDATE "Clients"
SET 
    "AllowLocalLogin" = true,
    "AllowExternalIdp" = true,
    "AllowQrLogin" = true
WHERE "ClientId" = 'YOUR_CLIENT_ID';

-- 2.2: Enable only external IdP and local login (no QR)
UPDATE "Clients"
SET 
    "AllowLocalLogin" = true,
    "AllowExternalIdp" = true,
    "AllowQrLogin" = false
WHERE "ClientId" = 'YOUR_CLIENT_ID';

-- 2.3: Add a provider mapping for a client
--      This maps an external provider to the client representing the upstream IdP
--      Replace the GUIDs with actual IDs from your database
INSERT INTO "ClientIdentityProviders" 
    ("ClientId", "IdentityProviderId", "Enabled", "AutoRedirectIfSingle", "Order")
VALUES 
    (
        (SELECT "Id" FROM "Clients" WHERE "ClientId" = 'YOUR_CLIENT_ID'),
        (SELECT "Id" FROM "IdentityProviders" WHERE "Name" = 'YOUR_PROVIDER_NAME'),
        true,
        false, -- Set to true if you want auto-redirect when it's the only provider
        1      -- Adjust order as needed
    );

-- 2.4: Update existing provider mapping settings
UPDATE "ClientIdentityProviders"
SET 
    "Enabled" = true,
    "AutoRedirectIfSingle" = false,
    "Order" = 1
WHERE "ClientId" = (SELECT "Id" FROM "Clients" WHERE "ClientId" = 'YOUR_CLIENT_ID')
  AND "IdentityProviderId" = (SELECT "Id" FROM "IdentityProviders" WHERE "Name" = 'YOUR_PROVIDER_NAME');

-- ==============================================================================
-- SECTION 3: Verification Queries
-- ==============================================================================

-- 3.1: Verify client configuration was updated
SELECT 
    "ClientId",
    "AllowLocalLogin",
    "AllowExternalIdp",
    "AllowQrLogin"
FROM "Clients"
WHERE "ClientId" = 'YOUR_CLIENT_ID';

-- 3.2: Verify provider mappings exist and are enabled
SELECT 
    c."ClientId",
    ip."Name" AS "ProviderName",
    cip."Enabled",
    cip."AutoRedirectIfSingle",
    cip."Order"
FROM "ClientIdentityProviders" cip
JOIN "Clients" c ON c."Id" = cip."ClientId"
JOIN "IdentityProviders" ip ON ip."Id" = cip."IdentityProviderId"
WHERE c."ClientId" = 'YOUR_CLIENT_ID'
ORDER BY cip."Order";

-- 3.3: List all available login methods for a client (combined view)
SELECT 
    c."ClientId",
    c."AllowLocalLogin" AS "LocalEnabled",
    c."AllowQrLogin" AS "QrEnabled",
    COALESCE(
        (SELECT COUNT(*) FROM "ClientIdentityProviders" cip2 
         WHERE cip2."ClientId" = c."Id" AND cip2."Enabled" = true), 
        0
    ) AS "ExternalProvidersCount"
FROM "Clients" c
WHERE c."ClientId" = 'YOUR_CLIENT_ID';

-- ==============================================================================
-- SECTION 4: Common Scenarios
-- ==============================================================================

-- Scenario A: Find and fix a client that should support all login methods
-- Step 1: Find the client
SELECT "ClientId", "AllowLocalLogin", "AllowExternalIdp", "AllowQrLogin" 
FROM "Clients" 
WHERE "ClientId" = 'idp1-client';

-- Step 2: Enable all methods
UPDATE "Clients" 
SET "AllowLocalLogin" = true, "AllowExternalIdp" = true, "AllowQrLogin" = true 
WHERE "ClientId" = 'idp1-client';

-- Step 3: Verify
SELECT "ClientId", "AllowLocalLogin", "AllowExternalIdp", "AllowQrLogin" 
FROM "Clients" 
WHERE "ClientId" = 'idp1-client';

-- Scenario B: Map Azure AD provider to a client representing an upstream IdP
-- Step 1: Find the client and provider IDs
SELECT 
    'Client' AS "Type",
    "Id",
    "ClientId" AS "Name"
FROM "Clients" 
WHERE "ClientId" = 'idp1-client'
UNION ALL
SELECT 
    'Provider' AS "Type",
    "Id",
    "Name"
FROM "IdentityProviders" 
WHERE "Name" = 'azure-ad';

-- Step 2: Create the mapping (replace GUIDs from Step 1)
INSERT INTO "ClientIdentityProviders" 
    ("ClientId", "IdentityProviderId", "Enabled", "AutoRedirectIfSingle", "Order")
VALUES 
    (
        (SELECT "Id" FROM "Clients" WHERE "ClientId" = 'idp1-client'),
        (SELECT "Id" FROM "IdentityProviders" WHERE "Name" = 'azure-ad'),
        true,
        false,
        1
    );

-- Step 3: Verify the mapping
SELECT 
    c."ClientId",
    ip."Name" AS "ProviderName",
    cip."Enabled",
    cip."Order"
FROM "ClientIdentityProviders" cip
JOIN "Clients" c ON c."Id" = cip."ClientId"
JOIN "IdentityProviders" ip ON ip."Id" = cip."IdentityProviderId"
WHERE c."ClientId" = 'idp1-client';

-- ==============================================================================
-- SECTION 5: Bulk Operations (Use with caution!)
-- ==============================================================================

-- 5.1: Enable external IdPs for all clients (use only in dev/test environments)
-- CAUTION: This affects ALL clients
UPDATE "Clients"
SET "AllowExternalIdp" = true;

-- 5.2: Find all clients that have no login methods enabled
SELECT "ClientId", "ClientName"
FROM "Clients"
WHERE "AllowLocalLogin" = false 
  AND "AllowExternalIdp" = false 
  AND "AllowQrLogin" = false;

-- 5.3: Enable at least local login for clients with no methods
UPDATE "Clients"
SET "AllowLocalLogin" = true
WHERE "AllowLocalLogin" = false 
  AND "AllowExternalIdp" = false 
  AND "AllowQrLogin" = false;
