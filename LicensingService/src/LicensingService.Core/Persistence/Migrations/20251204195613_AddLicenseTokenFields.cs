using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LicensingService.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLicenseTokenFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SignedToken",
                table: "Licenses",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SigningKeyId",
                table: "Licenses",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SignedToken",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "SigningKeyId",
                table: "Licenses");
        }
    }
}
