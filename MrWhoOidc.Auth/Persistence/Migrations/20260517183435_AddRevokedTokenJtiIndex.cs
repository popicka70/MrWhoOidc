using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRevokedTokenJtiIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Tokens_Type_Jti_TenantId",
                table: "Tokens",
                columns: new[] { "Type", "Jti", "TenantId" },
                filter: "\"Jti\" IS NOT NULL AND \"RevokedAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tokens_Type_Jti_TenantId",
                table: "Tokens");
        }
    }
}
