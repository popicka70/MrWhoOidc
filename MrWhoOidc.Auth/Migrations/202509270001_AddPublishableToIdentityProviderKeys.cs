using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Migrations
{
    public partial class AddPublishableToIdentityProviderKeys : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Publishable",
                table: "IdentityProviderKeys",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Optional performance index combining publish-related filters
            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviderKeys_Provider_Active_Publishable",
                table: "IdentityProviderKeys",
                columns: new[] { "IdentityProviderId", "Active", "Publishable" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IdentityProviderKeys_Provider_Active_Publishable",
                table: "IdentityProviderKeys");

            migrationBuilder.DropColumn(
                name: "Publishable",
                table: "IdentityProviderKeys");
        }
    }
}
