using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantCreationFieldsToRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTenantAdmin",
                table: "Registrations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TenantDescription",
                table: "Registrations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "Registrations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantSlug",
                table: "Registrations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_TenantSlug_Unique",
                table: "Registrations",
                column: "TenantSlug",
                unique: true,
                filter: "\"TenantSlug\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Registrations_TenantSlug_Unique",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "IsTenantAdmin",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "TenantDescription",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "TenantName",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "TenantSlug",
                table: "Registrations");
        }
    }
}
