using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformRegistrationScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPlatformRegistration",
                table: "Registrations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_IsPlatformRegistration_State",
                table: "Registrations",
                columns: new[] { "IsPlatformRegistration", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Registrations_IsPlatformRegistration_State",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "IsPlatformRegistration",
                table: "Registrations");
        }
    }
}
