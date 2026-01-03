using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserInfoJwtResponseMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdTokenEncryptedResponseAlg",
                table: "Clients",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdTokenEncryptedResponseEnc",
                table: "Clients",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserInfoEncryptedResponseAlg",
                table: "Clients",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserInfoEncryptedResponseEnc",
                table: "Clients",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserInfoSignedResponseAlg",
                table: "Clients",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IdTokenEncryptedResponseAlg",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "IdTokenEncryptedResponseEnc",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "UserInfoEncryptedResponseAlg",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "UserInfoEncryptedResponseEnc",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "UserInfoSignedResponseAlg",
                table: "Clients");
        }
    }
}
