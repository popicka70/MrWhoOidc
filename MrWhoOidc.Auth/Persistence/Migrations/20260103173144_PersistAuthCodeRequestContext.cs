using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistAuthCodeRequestContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AuthTime",
                table: "AuthorizationCodes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimsJson",
                table: "AuthorizationCodes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Resource",
                table: "AuthorizationCodes",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthTime",
                table: "AuthorizationCodes");

            migrationBuilder.DropColumn(
                name: "ClaimsJson",
                table: "AuthorizationCodes");

            migrationBuilder.DropColumn(
                name: "Resource",
                table: "AuthorizationCodes");
        }
    }
}
