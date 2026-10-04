using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistAuthorizationCodeLoginContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MappedClaimsJson",
                table: "AuthorizationCodes",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Sid",
                table: "AuthorizationCodes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpstreamAcr",
                table: "AuthorizationCodes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpstreamAmr",
                table: "AuthorizationCodes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpstreamIdp",
                table: "AuthorizationCodes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MappedClaimsJson",
                table: "AuthorizationCodes");

            migrationBuilder.DropColumn(
                name: "Sid",
                table: "AuthorizationCodes");

            migrationBuilder.DropColumn(
                name: "UpstreamAcr",
                table: "AuthorizationCodes");

            migrationBuilder.DropColumn(
                name: "UpstreamAmr",
                table: "AuthorizationCodes");

            migrationBuilder.DropColumn(
                name: "UpstreamIdp",
                table: "AuthorizationCodes");
        }
    }
}
