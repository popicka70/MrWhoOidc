using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BindQrLoginToInitiator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InitiatorIpAddress",
                table: "QrLoginSessions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InitiatorSecretHash",
                table: "QrLoginSessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InitiatorUserAgent",
                table: "QrLoginSessions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchCode",
                table: "QrLoginSessions",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InitiatorIpAddress",
                table: "QrLoginSessions");

            migrationBuilder.DropColumn(
                name: "InitiatorSecretHash",
                table: "QrLoginSessions");

            migrationBuilder.DropColumn(
                name: "InitiatorUserAgent",
                table: "QrLoginSessions");

            migrationBuilder.DropColumn(
                name: "MatchCode",
                table: "QrLoginSessions");
        }
    }
}
