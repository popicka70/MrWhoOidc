using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantScopedScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsGlobal",
                table: "Scopes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Scopes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Scopes_Name",
                table: "Scopes",
                column: "Name",
                unique: true,
                filter: "[TenantId] IS NULL AND [IsGlobal] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Scopes_TenantId",
                table: "Scopes",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Scopes_TenantId_Name",
                table: "Scopes",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "[TenantId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Scopes_Tenants_TenantId",
                table: "Scopes",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Scopes_Tenants_TenantId",
                table: "Scopes");

            migrationBuilder.DropIndex(
                name: "IX_Scopes_Name",
                table: "Scopes");

            migrationBuilder.DropIndex(
                name: "IX_Scopes_TenantId",
                table: "Scopes");

            migrationBuilder.DropIndex(
                name: "IX_Scopes_TenantId_Name",
                table: "Scopes");

            migrationBuilder.DropColumn(
                name: "IsGlobal",
                table: "Scopes");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Scopes");
        }
    }
}
