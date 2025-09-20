using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UpdateParEntityIdBased : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PushedAuthorizationRequests_RequestUri",
                table: "PushedAuthorizationRequests");

            migrationBuilder.AlterColumn<string>(
                name: "RequestUri",
                table: "PushedAuthorizationRequests",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(300)",
                oldMaxLength: 300);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "RequestUri",
                table: "PushedAuthorizationRequests",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushedAuthorizationRequests_RequestUri",
                table: "PushedAuthorizationRequests",
                column: "RequestUri",
                unique: true);
        }
    }
}
