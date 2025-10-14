using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexForPublicJwksCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add composite index for optimized JWKS lookup
            // Supports PublicJwksCache queries filtering by (IdentityProviderId, Active=true, Publishable=true, Purpose=Signing)
            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviderKeys_IdentityProviderId_Active_Publishable_Purpose",
                table: "IdentityProviderKeys",
                columns: new[] { "IdentityProviderId", "Active", "Publishable", "Purpose" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IdentityProviderKeys_IdentityProviderId_Active_Publishable_Purpose",
                table: "IdentityProviderKeys");
        }
    }
}
