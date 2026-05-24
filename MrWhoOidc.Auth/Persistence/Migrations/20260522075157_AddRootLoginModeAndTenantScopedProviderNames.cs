using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRootLoginModeAndTenantScopedProviderNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IdentityProviders_Name",
                table: "IdentityProviders");

            migrationBuilder.AddColumn<int>(
                name: "RootLoginMode",
                table: "PlatformSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviders_TenantId_Name",
                table: "IdentityProviders",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IdentityProviders_TenantId_Name",
                table: "IdentityProviders");

            migrationBuilder.DropColumn(
                name: "RootLoginMode",
                table: "PlatformSettings");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviders_Name",
                table: "IdentityProviders",
                column: "Name",
                unique: true);
        }
    }
}
