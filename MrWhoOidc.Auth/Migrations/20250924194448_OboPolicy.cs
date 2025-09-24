using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Migrations
{
    /// <inheritdoc />
    public partial class OboPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActJson",
                table: "Tokens",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DelegationDepth",
                table: "Tokens",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "OboAllowedCallersJson",
                table: "Clients",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OboAllowedScopesJson",
                table: "Clients",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OboAllowedSourceAudiencesJson",
                table: "Clients",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OboAllowedTargetAudiencesJson",
                table: "Clients",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OboDpopMode",
                table: "Clients",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OboEnabled",
                table: "Clients",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OboMaxDelegationDepth",
                table: "Clients",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OboMaxLifetimeMinutes",
                table: "Clients",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActJson",
                table: "Tokens");

            migrationBuilder.DropColumn(
                name: "DelegationDepth",
                table: "Tokens");

            migrationBuilder.DropColumn(
                name: "OboAllowedCallersJson",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "OboAllowedScopesJson",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "OboAllowedSourceAudiencesJson",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "OboAllowedTargetAudiencesJson",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "OboDpopMode",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "OboEnabled",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "OboMaxDelegationDepth",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "OboMaxLifetimeMinutes",
                table: "Clients");
        }
    }
}
