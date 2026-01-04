using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClientAuthorizationResponseCrypto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthorizationEncryptedResponseAlg",
                table: "Clients",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthorizationEncryptedResponseEnc",
                table: "Clients",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthorizationSignedResponseAlg",
                table: "Clients",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthorizationEncryptedResponseAlg",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "AuthorizationEncryptedResponseEnc",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "AuthorizationSignedResponseAlg",
                table: "Clients");
        }
    }
}
