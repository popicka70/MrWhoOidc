using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAllowRegistrationToIdentityProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowRegistration",
                table: "IdentityProviders",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowRegistration",
                table: "IdentityProviders");
        }
    }
}
