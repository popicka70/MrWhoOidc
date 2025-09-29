using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Migrations
{
    /// <inheritdoc />
    public partial class EmailNormalization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_Email",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_UserAlternativeEmails_UserId_Email",
                table: "UserAlternativeEmails");

            migrationBuilder.DropIndex(
                name: "IX_Registrations_Email",
                table: "Registrations");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                table: "Users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                table: "UserAlternativeEmails",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                table: "Registrations",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.Sql("UPDATE \"Users\" SET \"NormalizedEmail\" = LOWER(TRIM(\"Email\")) WHERE \"Email\" IS NOT NULL AND TRIM(\"Email\") <> ''; ");
            migrationBuilder.Sql("UPDATE \"UserAlternativeEmails\" SET \"NormalizedEmail\" = LOWER(TRIM(\"Email\")) WHERE TRIM(\"Email\") <> ''; ");
            migrationBuilder.Sql("UPDATE \"Registrations\" SET \"NormalizedEmail\" = LOWER(TRIM(\"Email\")) WHERE TRIM(\"Email\") <> ''; ");

            migrationBuilder.AlterColumn<string>(
                name: "NormalizedEmail",
                table: "UserAlternativeEmails",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "NormalizedEmail",
                table: "Registrations",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedEmail",
                table: "Users",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserAlternativeEmails_NormalizedEmail",
                table: "UserAlternativeEmails",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserAlternativeEmails_UserId_NormalizedEmail",
                table: "UserAlternativeEmails",
                columns: new[] { "UserId", "NormalizedEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_NormalizedEmail",
                table: "Registrations",
                column: "NormalizedEmail");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserAlternativeEmails_NormalizedEmail",
                table: "UserAlternativeEmails");

            migrationBuilder.DropIndex(
                name: "IX_UserAlternativeEmails_UserId_NormalizedEmail",
                table: "UserAlternativeEmails");

            migrationBuilder.DropIndex(
                name: "IX_Users_NormalizedEmail",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Registrations_NormalizedEmail",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "NormalizedEmail",
                table: "UserAlternativeEmails");

            migrationBuilder.DropColumn(
                name: "NormalizedEmail",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "NormalizedEmail",
                table: "Registrations");

            migrationBuilder.CreateIndex(
                name: "IX_UserAlternativeEmails_UserId_Email",
                table: "UserAlternativeEmails",
                columns: new[] { "UserId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_Email",
                table: "Registrations",
                column: "Email");
        }
    }
}
