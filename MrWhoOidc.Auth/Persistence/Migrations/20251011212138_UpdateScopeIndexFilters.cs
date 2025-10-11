using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UpdateScopeIndexFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Scopes_Name",
                table: "Scopes");

            migrationBuilder.DropIndex(
                name: "IX_Scopes_TenantId_Name",
                table: "Scopes");

            migrationBuilder.CreateIndex(
                name: "IX_Scopes_Name",
                table: "Scopes",
                column: "Name",
                unique: true,
                filter: "\"TenantId\" IS NULL AND \"IsGlobal\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_Scopes_TenantId_Name",
                table: "Scopes",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "\"TenantId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Scopes_Name",
                table: "Scopes");

            migrationBuilder.DropIndex(
                name: "IX_Scopes_TenantId_Name",
                table: "Scopes");

            migrationBuilder.CreateIndex(
                name: "IX_Scopes_Name",
                table: "Scopes",
                column: "Name",
                unique: true,
                filter: "[TenantId] IS NULL AND [IsGlobal] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Scopes_TenantId_Name",
                table: "Scopes",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "[TenantId] IS NOT NULL");
        }
    }
}
