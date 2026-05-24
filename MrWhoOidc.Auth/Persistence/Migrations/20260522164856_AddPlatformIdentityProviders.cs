using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformIdentityProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IdentityProviders_TenantId_Name",
                table: "IdentityProviders");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "IdentityProviders",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviders_Platform_Name",
                table: "IdentityProviders",
                column: "Name",
                unique: true,
                filter: "\"TenantId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviders_TenantId_Name",
                table: "IdentityProviders",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "\"TenantId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IdentityProviders_Platform_Name",
                table: "IdentityProviders");

            migrationBuilder.DropIndex(
                name: "IX_IdentityProviders_TenantId_Name",
                table: "IdentityProviders");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "IdentityProviders",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviders_TenantId_Name",
                table: "IdentityProviders",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }
    }
}
