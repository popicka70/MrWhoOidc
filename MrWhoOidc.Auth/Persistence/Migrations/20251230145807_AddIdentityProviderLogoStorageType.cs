using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityProviderLogoStorageType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LogoStorageType",
                table: "IdentityProviders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill existing data:
            // - Prefer DB-stored logo when LogoData exists
            // - Otherwise treat LogoUrl as external
            migrationBuilder.Sql("""
UPDATE "IdentityProviders"
SET "LogoStorageType" = CASE
        WHEN "LogoData" IS NOT NULL AND octet_length("LogoData") > 0 THEN 1
        WHEN "LogoUrl" IS NOT NULL AND btrim("LogoUrl") <> '' THEN 2
        ELSE 0
END;

UPDATE "IdentityProviders"
SET "LogoUrl" = NULL
WHERE "LogoStorageType" = 1
    AND "LogoUrl" ILIKE '/api/providers/%';
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LogoStorageType",
                table: "IdentityProviders");
        }
    }
}
