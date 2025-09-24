using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Migrations
{
    /// <inheritdoc />
    public partial class M2MPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowClientSecretBasic",
                table: "Clients",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowClientSecretPost",
                table: "Clients",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowPrivateKeyJwt",
                table: "Clients",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "M2MAccessTokenLifetimeSeconds",
                table: "Clients",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "M2MAllowedAudiencesJson",
                table: "Clients",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "M2MMtlsThumbprintsJson",
                table: "Clients",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowClientSecretBasic",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "AllowClientSecretPost",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "AllowPrivateKeyJwt",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "M2MAccessTokenLifetimeSeconds",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "M2MAllowedAudiencesJson",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "M2MMtlsThumbprintsJson",
                table: "Clients");
        }
    }
}
