using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProtectDeviceCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DeviceCodes_DeviceCode",
                table: "DeviceCodes");

            migrationBuilder.DropIndex(
                name: "IX_DeviceCodes_UserCode",
                table: "DeviceCodes");

            migrationBuilder.AlterColumn<string>(
                name: "UserCode",
                table: "DeviceCodes",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<string>(
                name: "DeviceCode",
                table: "DeviceCodes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AddColumn<string>(
                name: "DeviceCodeHash",
                table: "DeviceCodes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserCodeProtected",
                table: "DeviceCodes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCodes_DeviceCode",
                table: "DeviceCodes",
                column: "DeviceCode",
                unique: true,
                filter: "\"DeviceCode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCodes_DeviceCodeHash",
                table: "DeviceCodes",
                column: "DeviceCodeHash",
                unique: true,
                filter: "\"DeviceCodeHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCodes_UserCode",
                table: "DeviceCodes",
                column: "UserCode",
                unique: true,
                filter: "\"UserCode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCodes_UserCodeProtected",
                table: "DeviceCodes",
                column: "UserCodeProtected",
                unique: true,
                filter: "\"UserCodeProtected\" IS NOT NULL");

            // The codes of rows that are already expired are dropped: those requests can no longer be used, and the
            // codes must not stay in the database. Rows that are still live keep them, so a pod of the previous
            // version can still serve a poll that is in flight during a rolling deploy.
            migrationBuilder.Sql(@"UPDATE ""DeviceCodes"" SET ""DeviceCode"" = NULL, ""UserCode"" = NULL WHERE ""ExpiresAt"" < now()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DeviceCodes_DeviceCode",
                table: "DeviceCodes");

            migrationBuilder.DropIndex(
                name: "IX_DeviceCodes_DeviceCodeHash",
                table: "DeviceCodes");

            migrationBuilder.DropIndex(
                name: "IX_DeviceCodes_UserCode",
                table: "DeviceCodes");

            migrationBuilder.DropIndex(
                name: "IX_DeviceCodes_UserCodeProtected",
                table: "DeviceCodes");

            migrationBuilder.DropColumn(
                name: "DeviceCodeHash",
                table: "DeviceCodes");

            migrationBuilder.DropColumn(
                name: "UserCodeProtected",
                table: "DeviceCodes");

            // The rows that no longer carry a code cannot be served by the previous version (it looks the codes up
            // by their value), so they are removed instead of being given a placeholder that would not be unique.
            migrationBuilder.Sql(@"DELETE FROM ""DeviceCodes"" WHERE ""DeviceCode"" IS NULL OR ""UserCode"" IS NULL");

            migrationBuilder.AlterColumn<string>(
                name: "UserCode",
                table: "DeviceCodes",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "DeviceCode",
                table: "DeviceCodes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCodes_DeviceCode",
                table: "DeviceCodes",
                column: "DeviceCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCodes_UserCode",
                table: "DeviceCodes",
                column: "UserCode",
                unique: true);
        }
    }
}
