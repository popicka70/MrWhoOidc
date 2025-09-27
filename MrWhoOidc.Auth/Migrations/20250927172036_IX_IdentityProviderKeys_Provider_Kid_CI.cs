using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Migrations
{
    /// <inheritdoc />
    public partial class IX_IdentityProviderKeys_Provider_Kid_CI : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IdentityProviderKeys_IdentityProviderId",
                table: "IdentityProviderKeys");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviderKeys_Provider_Kid_CI",
                table: "IdentityProviderKeys",
                columns: new[] { "IdentityProviderId", "Kid" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IdentityProviderKeys_Provider_Kid_CI",
                table: "IdentityProviderKeys");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviderKeys_IdentityProviderId",
                table: "IdentityProviderKeys",
                column: "IdentityProviderId");
        }
    }
}
