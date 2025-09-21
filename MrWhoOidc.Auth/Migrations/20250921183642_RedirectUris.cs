using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Migrations
{
    /// <inheritdoc />
    public partial class RedirectUris : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedLoginRedirectUrisJson",
                table: "Clients",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AllowedLogoutRedirectUrisJson",
                table: "Clients",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedLoginRedirectUrisJson",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "AllowedLogoutRedirectUrisJson",
                table: "Clients");
        }
    }
}
