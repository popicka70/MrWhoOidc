using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Migrations
{
    /// <inheritdoc />
    public partial class UserTotp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TotpSecret",
                table: "Users",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TotpEnabled",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TotpSecret",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TotpEnabled",
                table: "Users");
        }
    }
}
