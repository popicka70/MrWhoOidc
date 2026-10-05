using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <summary>
    /// R7 / #3 / R10 (assessment 2026-10-04). Additive and rolling-deploy safe:
    /// <list type="bullet">
    /// <item>Client grant flags (AllowClientCredentials/AllowDeviceAuthorization/AllowCiba) now DEFAULT false for
    /// NEW rows; existing rows keep their stored values.</item>
    /// <item>New nullable column Clients.RegistrationSource, backfilled to 'dcr' (dynamically registered clients)
    /// and 'cli' (per-tenant MrWho CLI system clients).</item>
    /// <item>Data backfill that preserves the behaviour of existing clients under the new default-deny rules:
    /// clients without any ClientScopes row get every scope visible to their tenant (global + own), except the
    /// protected 'tenants' and 'mrwho:admin' scopes that always required explicit assignment; clients with a NULL/blank GrantTypesJson
    /// get the grants they could use before (authorization_code, refresh_token, plus client_credentials /
    /// device_code / ciba when the matching Allow* flag is set, plus token-exchange unless OBO was explicitly
    /// disabled).</item>
    /// </list>
    /// </summary>
    public partial class ClientScopeAndGrantDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "AllowDeviceAuthorization",
                table: "Clients",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: true);

            migrationBuilder.AlterColumn<bool>(
                name: "AllowClientCredentials",
                table: "Clients",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: true);

            migrationBuilder.AlterColumn<bool>(
                name: "AllowCiba",
                table: "Clients",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "RegistrationSource",
                table: "Clients",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            // R7: the standard OIDC scopes must exist (ClientScopes.ScopeName is a FK to Scopes.Name).
            migrationBuilder.Sql("""
                INSERT INTO "Scopes" ("Name", "TenantId", "IsGlobal", "Description", "IsExposed")
                SELECT v.name, NULL, TRUE, 'Standard scope ' || v.name, TRUE
                FROM (VALUES ('openid'), ('profile'), ('email'), ('offline_access'), ('roles')) AS v(name)
                WHERE NOT EXISTS (SELECT 1 FROM "Scopes" s WHERE s."Name" = v.name);
                """);

            // R7: a client with no ClientScopes rows used to be allowed ANY scope. Preserve that for existing clients
            // by assigning every scope visible to the client's tenant, except the protected 'tenants' and 'mrwho:admin' scopes (which
            // always required explicit assignment; ADR-0010 / migration AdminApiClientFlag removed mrwho:admin assignments).
            migrationBuilder.Sql("""
                INSERT INTO "ClientScopes" ("ClientId", "ScopeName")
                SELECT c."Id", s."Name"
                FROM "Clients" c
                JOIN "Scopes" s ON s."TenantId" IS NULL OR s."TenantId" = c."TenantId"
                WHERE s."Name" NOT IN ('tenants', 'mrwho:admin')
                  AND NOT EXISTS (SELECT 1 FROM "ClientScopes" cs WHERE cs."ClientId" = c."Id")
                ON CONFLICT DO NOTHING;
                """);

            // #3: a NULL GrantTypesJson used to mean "every grant"; it now means authorization_code + refresh_token.
            // Record the grants existing clients could actually use so their behaviour is unchanged.
            migrationBuilder.Sql("""
                UPDATE "Clients" c
                SET "GrantTypesJson" = (
                    SELECT json_agg(v.grant_type ORDER BY v.ord)::text
                    FROM (VALUES
                        (1, 'authorization_code', TRUE),
                        (2, 'refresh_token', TRUE),
                        (3, 'client_credentials', c."AllowClientCredentials"),
                        (4, 'urn:ietf:params:oauth:grant-type:device_code', c."AllowDeviceAuthorization"),
                        (5, 'urn:openid:params:grant-type:ciba', c."AllowCiba"),
                        (6, 'urn:ietf:params:oauth:grant-type:token-exchange', COALESCE(c."OboEnabled", TRUE))
                    ) AS v(ord, grant_type, enabled)
                    WHERE v.enabled)
                WHERE c."GrantTypesJson" IS NULL OR btrim(c."GrantTypesJson") = '';
                """);

            // R10: attribute existing clients where the origin is certain.
            migrationBuilder.Sql("""
                UPDATE "Clients" c
                SET "RegistrationSource" = 'dcr'
                WHERE c."RegistrationSource" IS NULL
                  AND (EXISTS (SELECT 1 FROM "DynamicRegistrationTokens" t WHERE t."ClientId" = c."ClientId")
                       OR c."ClientId" LIKE 'dyn\_%');
                """);
            migrationBuilder.Sql("""
                UPDATE "Clients"
                SET "RegistrationSource" = 'cli'
                WHERE "RegistrationSource" IS NULL
                  AND "IsSystemClient" = TRUE
                  AND "ClientId" LIKE 'mrwho-cli-%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The ClientScopes / GrantTypesJson backfills are intentionally not reverted: they encode the behaviour
            // those clients already had, and the previous code treats them identically.
            migrationBuilder.DropColumn(
                name: "RegistrationSource",
                table: "Clients");

            migrationBuilder.AlterColumn<bool>(
                name: "AllowDeviceAuthorization",
                table: "Clients",
                type: "boolean",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: false);

            migrationBuilder.AlterColumn<bool>(
                name: "AllowClientCredentials",
                table: "Clients",
                type: "boolean",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: false);

            migrationBuilder.AlterColumn<bool>(
                name: "AllowCiba",
                table: "Clients",
                type: "boolean",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: false);
        }
    }
}
