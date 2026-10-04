using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenCnfX5tS256 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CnfX5tS256",
                table: "Tokens",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // No backfill. Existing client_credentials rows that were bound with mTLS hold the certificate
            // thumbprint in CnfJkt, but nothing on the row tells them apart from DPoP-bound rows. These are
            // short-lived access tokens; until they expire their introspection still reports cnf.jkt as before.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CnfX5tS256",
                table: "Tokens");
        }
    }
}
