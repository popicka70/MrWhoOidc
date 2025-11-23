using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.KeyGen.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLicenseScopeMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultTenantFeatures",
                table: "LicenseTokenMetadata",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuedTo",
                table: "LicenseTokenMetadata",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "LicenseTokenMetadata",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "LicenseTokenMetadata",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantSlug",
                table: "LicenseTokenMetadata",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.Sql("UPDATE \"LicenseTokenMetadata\" SET \"Scope\" = 'platform' WHERE IFNULL(\"Scope\", '') = '';");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseTokenMetadata_Scope",
                table: "LicenseTokenMetadata",
                column: "Scope");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LicenseTokenMetadata_Scope",
                table: "LicenseTokenMetadata");

            migrationBuilder.DropColumn(
                name: "DefaultTenantFeatures",
                table: "LicenseTokenMetadata");

            migrationBuilder.DropColumn(
                name: "IssuedTo",
                table: "LicenseTokenMetadata");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "LicenseTokenMetadata");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "LicenseTokenMetadata");

            migrationBuilder.DropColumn(
                name: "TenantSlug",
                table: "LicenseTokenMetadata");
        }
    }
}
