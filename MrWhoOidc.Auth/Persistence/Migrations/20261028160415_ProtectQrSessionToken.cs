using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProtectQrSessionToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QrLoginSessions_SessionToken",
                table: "QrLoginSessions");

            migrationBuilder.DropIndex(
                name: "IX_QrLoginSessions_SessionTokenHash",
                table: "QrLoginSessions");

            migrationBuilder.AlterColumn<string>(
                name: "SessionToken",
                table: "QrLoginSessions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.CreateIndex(
                name: "IX_QrLoginSessions_SessionToken",
                table: "QrLoginSessions",
                column: "SessionToken",
                unique: true,
                filter: "\"SessionToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_QrLoginSessions_SessionTokenHash",
                table: "QrLoginSessions",
                column: "SessionTokenHash",
                unique: true);

            // The plaintext token of rows that are already expired is dropped: those sessions can no longer be used,
            // and their token must not stay in the database. Rows that are still live keep it, so a pod of the
            // previous version can still serve a login that is in flight during a rolling deploy.
            migrationBuilder.Sql(@"UPDATE ""QrLoginSessions"" SET ""SessionToken"" = NULL WHERE ""ExpiresAt"" < now()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QrLoginSessions_SessionToken",
                table: "QrLoginSessions");

            migrationBuilder.DropIndex(
                name: "IX_QrLoginSessions_SessionTokenHash",
                table: "QrLoginSessions");

            // The old schema requires a unique token on every row. The rows that no longer carry one get a
            // placeholder, so the unique index can be recreated.
            migrationBuilder.Sql(@"UPDATE ""QrLoginSessions"" SET ""SessionToken"" = 'legacy-' || ""Id""::text WHERE ""SessionToken"" IS NULL");

            migrationBuilder.AlterColumn<string>(
                name: "SessionToken",
                table: "QrLoginSessions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_QrLoginSessions_SessionToken",
                table: "QrLoginSessions",
                column: "SessionToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QrLoginSessions_SessionTokenHash",
                table: "QrLoginSessions",
                column: "SessionTokenHash");
        }
    }
}
