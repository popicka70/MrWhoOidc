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
