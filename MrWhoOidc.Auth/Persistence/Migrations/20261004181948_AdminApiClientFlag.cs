using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminApiClientFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowAdminApi",
                table: "Clients",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // ADR-0010: the existing CLI clients (system clients created by CliClientService) become the admin API
            // clients and get the admin scope. Nothing else is flagged.
            migrationBuilder.Sql("""
                UPDATE "Clients"
                SET "AllowAdminApi" = TRUE
                WHERE "IsSystemClient" = TRUE AND "ClientId" LIKE 'mrwho-cli-%';
                """);

            // ClientScopes.ScopeName is a FK to Scopes.Name: the scope row must exist before it is assigned. It is a
            // restricted scope, so it is not advertised in discovery (IsExposed = false).
            migrationBuilder.Sql("""
                INSERT INTO "Scopes" ("Name", "TenantId", "IsGlobal", "Description", "IsExposed")
                SELECT 'mrwho:admin', NULL, TRUE, 'MrWhoOidc admin API (ADR-0010)', FALSE
                WHERE NOT EXISTS (SELECT 1 FROM "Scopes" s WHERE s."Name" = 'mrwho:admin');
                """);

            migrationBuilder.Sql("""
                INSERT INTO "ClientScopes" ("ClientId", "ScopeName")
                SELECT c."Id", 'mrwho:admin'
                FROM "Clients" c
                WHERE c."AllowAdminApi" = TRUE
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM "ClientScopes" WHERE "ScopeName" = 'mrwho:admin';""");

            migrationBuilder.DropColumn(
                name: "AllowAdminApi",
                table: "Clients");
        }
    }
}
