using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddArtifactExpiryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Tokens_Type_ExpiresAt",
                table: "Tokens",
                columns: new[] { "Type", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailConfirmations_ExpiresAt",
                table: "EmailConfirmations",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCodes_ExpiresAt",
                table: "DeviceCodes",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationCodes_ExpiresAt",
                table: "AuthorizationCodes",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tokens_Type_ExpiresAt",
                table: "Tokens");

            migrationBuilder.DropIndex(
                name: "IX_EmailConfirmations_ExpiresAt",
                table: "EmailConfirmations");

            migrationBuilder.DropIndex(
                name: "IX_DeviceCodes_ExpiresAt",
                table: "DeviceCodes");

            migrationBuilder.DropIndex(
                name: "IX_AuthorizationCodes_ExpiresAt",
                table: "AuthorizationCodes");
        }
    }
}
